import { useState, useEffect, useId, useRef } from "react";
import { TaskCreateForm } from "./TaskCreationPage";
import { getAvailableRobotsForTask, assignRobotToTask, getTransportTaskDetailsById, updateTransportTask, type AvailableRobot, type ManualAssignTaskResponse, type TransportTaskDetails } from "../api/modules/tasks";
import { useData } from "../context/DataContext";
import { Priority, Task } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn, PriorityBadge, StatusBadge } from "../components/SharedUI";

// ══════════════════════════════════════════════════════════════════════════════
// Tasks Page — Full lifecycle management (list + create + edit + cancel + delete)
// ═══════════════════════════════════════════════════════════════════════════════

function errorMessage(error: unknown): string {
    if (error && typeof error === "object" && "message" in error && typeof (error as any).message === "string") {
        return (error as any).message as string;
    }
    if (error instanceof Error) return error.message;
    return "Something went wrong";
}

function formatPriorityLevel(value: unknown): string {
    if (value == null) return "—";
    const n = typeof value === "number" ? value : Number.parseInt(String(value), 10);
    if (Number.isFinite(n)) {
        if (n <= 1) return "HIGH";
        if (n === 2) return "MEDIUM";
        return "LOW";
    }
    const s = String(value).toUpperCase();
    if (s === "HIGH" || s === "MEDIUM" || s === "LOW") return s;
    return String(value);
}

function toLocalDateTimeValue(iso: string | null | undefined): string {
    if (!iso) return "";
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return "";
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function fromLocalDateTimeValue(local: string): string | null {
    const v = local.trim();
    if (!v) return null;
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return null;
    return d.toISOString();
}

interface EditState {
    warehouseId: string;
    pickupStationId: string;
    deliveryStationId: string;
    priority: Priority;
    packageCode: string;
    itemDescription: string;
    payloadWeightKg: string;
    scheduledTime: string;
}

const PRIORITY_ORDER: Record<Priority, number> = { HIGH: 0, MEDIUM: 1, LOW: 2 };

export function TasksPage() {
    const { tasks, robots, cancelTask, deleteTask, refreshTasks, isTasksLoading, tasksError, setTaskRobot } = useData();
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<string>("ALL");
    const [createOpen, setCreateOpen] = useState(false);
    const [assignTarget, setAssignTarget] = useState<Task | null>(null);
    const [editTarget, setEditTarget] = useState<Task | null>(null);
    const [editState, setEditState] = useState<EditState>({
        warehouseId: "",
        pickupStationId: "",
        deliveryStationId: "",
        priority: "MEDIUM",
        packageCode: "",
        itemDescription: "",
        payloadWeightKg: "",
        scheduledTime: "",
    });
    const [editDetails, setEditDetails] = useState<TransportTaskDetails | null>(null);
    const [editError, setEditError] = useState("");
    const [editLoading, setEditLoading] = useState(false);
    const [editBusy, setEditBusy] = useState(false);
    const [detailsTarget, setDetailsTarget] = useState<Task | null>(null);
    const [details, setDetails] = useState<TransportTaskDetails | null>(null);
    const [detailsError, setDetailsError] = useState("");
    const [detailsLoading, setDetailsLoading] = useState(false);
    const [deleteTarget, setDeleteTarget] = useState<Task | null>(null);
    const [isDeleting, setIsDeleting] = useState(false);
    const deleteInFlight = useRef(false);
    const [deleteError, setDeleteError] = useState("");
    const [toast, setToast] = useState<{ msg: string; type: "success" | "error" | "info" } | null>(null);

    const [isCreating, setIsCreating] = useState(false);
    const autoRefreshInFlight = useRef(false);

    useEffect(() => {
        const refreshSafely = async () => {
            if (autoRefreshInFlight.current) return;
            autoRefreshInFlight.current = true;
            try {
                await refreshTasks();
            } catch {
                // ignore background refresh failures; UI shows tasksError if needed
            } finally {
                autoRefreshInFlight.current = false;
            }
        };

        void refreshSafely();

        const handleWindowFocus = () => {
            void refreshSafely();
        };

        const handleVisibilityChange = () => {
            if (document.visibilityState === "visible") {
                void refreshSafely();
            }
        };

        window.addEventListener("focus", handleWindowFocus);
        document.addEventListener("visibilitychange", handleVisibilityChange);

        return () => {
            window.removeEventListener("focus", handleWindowFocus);
            document.removeEventListener("visibilitychange", handleVisibilityChange);
        };
    }, [refreshTasks]);

    // ── Filtering ──
    const searchedTasks = tasks.filter((t) => {
        const q = search.trim().toLowerCase();
        const matchSearch = !q
            || t.id.toLowerCase().includes(q)
            || t.pickup.toLowerCase().includes(q)
            || t.delivery.toLowerCase().includes(q)
            || t.packageInfo.toLowerCase().includes(q)
            || (t.robotId || "").toLowerCase().includes(q);
        return matchSearch;
    }).sort((a, b) => PRIORITY_ORDER[a.priority] - PRIORITY_ORDER[b.priority]
        || a.id.localeCompare(b.id, undefined, { numeric: true }));

    const queueTasks = searchedTasks.filter((t) => t.status !== "COMPLETED"
        && (statusFilter === "ALL" || t.status === statusFilter));
    const completedTasks = searchedTasks.filter((t) => t.status === "COMPLETED");
    const sections = [
        {
            id: "queue",
            title: "Task Queue",
            description: "Sorted by priority: HIGH > MEDIUM > LOW. Cancelled tasks remain available via the status filter.",
            items: queueTasks,
            total: tasks.filter((t) => t.status !== "COMPLETED").length,
            color: "#22d3ee",
            empty: search.trim() || statusFilter !== "ALL" ? "No queued tasks match your filters." : "No tasks in the queue.",
        },
        {
            id: "completed",
            title: "Completed Tasks",
            description: "Completed transport tasks only. Search applies here; queue status filters do not.",
            items: completedTasks,
            total: tasks.filter((t) => t.status === "COMPLETED").length,
            color: "#22c55e",
            empty: search.trim() ? "No completed tasks match your search." : "No completed tasks yet.",
        },
    ];
    const statusOptions = ["ALL", "PENDING", "QUEUED", "ASSIGNED", "EXECUTING", "CANCELLED"];
    const counts = {
        total: tasks.length,
        pending: tasks.filter((t) => t.status === "PENDING" || t.status === "QUEUED").length,
        executing: tasks.filter((t) => t.status === "EXECUTING" || t.status === "ASSIGNED").length,
        completed: tasks.filter((t) => t.status === "COMPLETED").length,
    };

    // ── Actions ──
    const openEdit = (task: Task) => {
        setEditTarget(task);
        setEditState({
            warehouseId: "",
            pickupStationId: "",
            deliveryStationId: "",
            priority: task.priority,
            packageCode: "",
            itemDescription: "",
            payloadWeightKg: "",
            scheduledTime: "",
        });
        setEditDetails(null);
        setEditError("");
        setEditLoading(false);
        if (task.taskId == null) {
            setEditError("Missing backend task ID. Reload the task list before editing.");
            return;
        }
        setEditLoading(true);
        getTransportTaskDetailsById(task.taskId).then((payload) => {
            setEditDetails(payload);
            setEditState((s) => ({
                ...s,
                warehouseId: payload.warehouseId != null ? String(payload.warehouseId) : "",
                pickupStationId: payload.pickupStationId != null ? String(payload.pickupStationId) : "",
                deliveryStationId: payload.deliveryStationId != null ? String(payload.deliveryStationId) : "",
                priority: task.priority,
                packageCode: payload.packageCode ? String(payload.packageCode) : "",
                itemDescription: payload.itemDescription ? String(payload.itemDescription) : "",
                payloadWeightKg: payload.payloadWeightKg != null ? String(payload.payloadWeightKg) : "",
                scheduledTime: toLocalDateTimeValue(payload.scheduledTime),
            }));
        }).catch((error) => {
            setEditError(errorMessage(error));
        }).finally(() => {
            setEditLoading(false);
        });
    };

    const confirmEdit = async () => {
        if (!editTarget || editBusy) return;
        if (editTarget.taskId == null) {
            setEditError("Missing backend task ID. Reload the task list before editing.");
            return;
        }
        const warehouseId = Number.parseInt(editState.warehouseId, 10);
        const pickupStationId = Number.parseInt(editState.pickupStationId, 10);
        const deliveryStationId = Number.parseInt(editState.deliveryStationId, 10);
        if (!Number.isFinite(warehouseId) || warehouseId <= 0) {
            setEditError("Warehouse ID is required.");
            return;
        }
        if (!Number.isFinite(pickupStationId) || pickupStationId <= 0) {
            setEditError("Pickup Station ID is required.");
            return;
        }
        if (!Number.isFinite(deliveryStationId) || deliveryStationId <= 0) {
            setEditError("Delivery Station ID is required.");
            return;
        }
        setEditBusy(true);
        setEditError("");
        try {
            const priorityLevel = editState.priority === "HIGH" ? 1 : editState.priority === "MEDIUM" ? 2 : 3;
            const payloadWeightKg = editState.payloadWeightKg.trim() ? Number.parseFloat(editState.payloadWeightKg) : null;
            if (payloadWeightKg != null && !Number.isFinite(payloadWeightKg)) {
                setEditError("Payload weight must be a number.");
                setEditBusy(false);
                return;
            }
            const scheduledTime = fromLocalDateTimeValue(editState.scheduledTime);
            await updateTransportTask(editTarget.taskId, {
                warehouseId,
                pickupStationId,
                deliveryStationId,
                priorityLevel,
                packageCode: editState.packageCode.trim() ? editState.packageCode.trim() : null,
                itemDescription: editState.itemDescription.trim() ? editState.itemDescription.trim() : null,
                payloadWeightKg,
                scheduledTime,
            });
            await refreshTasks(true);
            setEditTarget(null);
            setToast({ msg: `Task ${editTarget.id} updated successfully.`, type: "success" });
        } catch (error) {
            setEditError(errorMessage(error));
        } finally {
            setEditBusy(false);
        }
    };

    const handleCancel = (task: Task) => {
        cancelTask(task.id);
        setToast({ msg: `${task.id} cancelled.`, type: "info" });
    };

    const handleDelete = (task: Task) => {
        setDeleteError("");
        setDeleteTarget(task);
    };

    const handleReload = async () => {
        try {
            await refreshTasks(true);
            setToast({ msg: "Task list reloaded.", type: "success" });
        } catch {
            setToast({ msg: "Could not reload tasks.", type: "error" });
        }
    };

    const confirmDelete = async () => {
        if (!deleteTarget || deleteInFlight.current) return;
        deleteInFlight.current = true;
        setIsDeleting(true);
        setDeleteError("");
        try {
            await deleteTask(deleteTarget);
            setToast({ msg: `Task ${deleteTarget.id} deleted successfully.`, type: "success" });
            setDeleteTarget(null);
        } catch (error) {
            setDeleteError((error as { message?: string })?.message || "Unable to delete this task. Please try again.");
        } finally {
            deleteInFlight.current = false;
            setIsDeleting(false);
        }
    };

    const canEdit = (s: string) => s === "PENDING" || s === "QUEUED";
    const canCancel = (s: string) => s === "PENDING" || s === "QUEUED" || s === "ASSIGNED";

    useEffect(() => {
        if (!detailsTarget) return;
        setDetails(null);
        setDetailsError("");
        if (detailsTarget.taskId == null) {
            setDetailsError("Missing backend task ID. Reload the task list before viewing details.");
            setDetailsLoading(false);
            return;
        }
        let active = true;
        setDetailsLoading(true);
        getTransportTaskDetailsById(detailsTarget.taskId).then((payload) => {
            if (!active) return;
            setDetails(payload);
        }).catch((error) => {
            if (!active) return;
            setDetailsError(errorMessage(error));
        }).finally(() => {
            if (!active) return;
            setDetailsLoading(false);
        });
        return () => {
            active = false;
        };
    }, [detailsTarget]);

    return (
        <div
            className="flex flex-col h-full overflow-hidden transition-colors"
            style={{ background: "var(--background)", color: "var(--text-primary)" }}
        >
            {/* Header */}
            <PageHeader
                title="Transport Task Management"
                sub="Create · Edit · Cancel · Track transport tasks across all lifecycle states"
            >
                <button
                    onClick={handleReload}
                    disabled={isTasksLoading}
                    className="flex items-center gap-2 px-4 py-2 rounded-xl text-[13px] font-bold transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
                    style={{
                        background: "var(--surface-3)",
                        color: "var(--text-primary)",
                        border: "1px solid var(--border-medium)",
                    }}
                >
                    <Icon d={IC.refresh} size={15} />
                    {isTasksLoading ? "Refreshing..." : "Reload"}
                </button>
                <PrimaryBtn onClick={() => setCreateOpen(true)}>
                    <Icon d={IC.plus} size={15} /> Create Transport Task
                </PrimaryBtn>
            </PageHeader>

            {/* Stat strip */}
            <div
                className="shrink-0 flex flex-wrap gap-3 px-6 py-3"
                style={{ borderBottom: "1px solid var(--border-subtle)" }}
            >
                {[
                    { label: "Total Tasks", value: counts.total, color: "#94a3b8" },
                    { label: "Pending / Queued", value: counts.pending, color: "#60a5fa" },
                    { label: "Executing", value: counts.executing, color: "#22d3ee" },
                    { label: "Completed", value: counts.completed, color: "#22c55e" },
                ].map((s) => (
                    <div
                        key={s.label}
                        className="rounded-xl px-4 py-2.5 flex items-center gap-3 transition-colors"
                        style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                    >
                        <span className="font-mono text-[22px] font-bold" style={{ color: s.color }}>{s.value}</span>
                        <span className="text-[11px]" style={{ color: "var(--text-faint)" }}>{s.label}</span>
                    </div>
                ))}
            </div>

            {/* Toolbar */}
            <div
                className="shrink-0 flex flex-wrap items-center gap-3 px-6 py-3"
                style={{ borderBottom: "1px solid var(--border-subtle)" }}
            >
                <div className="relative" style={{ width: 320 }}>
                    <span
                        className="absolute left-3 top-1/2 -translate-y-1/2"
                        style={{ color: "var(--text-faint)" }}
                    >
                        <Icon d={IC.search} size={14} />
                    </span>
                    <input
                        aria-label="Search transport tasks"
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder="Search ID, station, package, robot..."
                        className="w-full pl-9 pr-3 py-2 rounded-lg text-[13px] outline-none transition-colors"
                        style={{
                            background: "var(--surface-3)",
                            border: "1px solid var(--border-medium)",
                            color: "var(--text-primary)",
                        }}
                    />
                </div>
                <div className="flex flex-wrap items-center gap-1">
                    <span className="mr-2 text-[11px]" style={{ color: "var(--text-faint)" }}>Queue status</span>
                    {statusOptions.map((s) => (
                        <button
                            key={s}
                            type="button"
                            aria-pressed={statusFilter === s}
                            onClick={() => setStatusFilter(s)}
                            className="font-mono text-[10px] font-bold px-2.5 py-1 rounded-md transition-all"
                            style={{
                                background: statusFilter === s ? "rgba(34,211,238,0.15)" : "transparent",
                                color: statusFilter === s ? "#22d3ee" : "var(--text-faint)",
                                border: `1px solid ${statusFilter === s ? "rgba(34,211,238,0.3)" : "var(--border-medium)"}`,
                            }}
                        >
                            {s}
                        </button>
                    ))}
                </div>
            </div>

            {tasksError && (
                <div className="px-6 pt-3">
                    <div
                        className="rounded-xl px-4 py-3 text-[12px]"
                        style={{
                            background: "rgba(239,68,68,0.08)",
                            border: "1px solid rgba(239,68,68,0.2)",
                            color: "#fca5a5",
                        }}
                    >
                        API sync issue: {tasksError}
                    </div>
                </div>
            )}

            {/* Separate queue and completed history, sharing the same task table. */}
            <div className="flex-1 min-h-0 overflow-auto px-6 pb-6 pt-3 space-y-5">
                {sections.map((section) => (
                <section
                    key={section.id}
                    aria-labelledby={`${section.id}-heading`}
                    aria-busy={isTasksLoading}
                    className="rounded-2xl overflow-hidden flex flex-col transition-colors"
                    style={{ background: "var(--surface-2)", border: `1px solid ${section.color}40` }}
                >
                    <div className="flex items-center gap-3 px-4 py-4" style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                        <div className="flex-1">
                            <h2 id={`${section.id}-heading`} className="text-[14px] font-bold flex items-center gap-2">
                                {section.id === "completed" && <Icon d={IC.check} size={15} />}
                                {section.title}
                            </h2>
                            <p className="mt-1 text-[11px]" style={{ color: "var(--text-faint)" }}>{section.description}</p>
                        </div>
                        <span className="rounded-lg px-3 py-1 font-mono text-[13px] font-bold" style={{ background: `${section.color}15`, color: section.color }}>
                            {section.items.length}
                        </span>
                    </div>
                    <div className="overflow-auto max-h-[480px]">
                        <table className="w-full border-collapse" style={{ minWidth: 1000 }}>
                            <thead className="sticky top-0 z-10">
                                <tr style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                                    {["Task ID", "Pickup", "Delivery", "Package", "Priority", "Assigned Robot", "Status", "Actions"].map((h) => (
                                        <th
                                            key={h}
                                            className="text-left px-4 py-3 font-mono text-[10px] font-semibold tracking-wider whitespace-nowrap"
                                            style={{ color: "var(--text-faint)" }}
                                        >
                                            {h}
                                        </th>
                                    ))}
                                </tr>
                            </thead>
                            <tbody>
                                {section.items.map((task) => (
                                    <tr
                                        key={task.id}
                                        style={{
                                            borderBottom: "1px solid var(--border-subtle)",
                                            background: task.status === "CANCELLED" ? "rgba(239,68,68,0.04)" : "transparent",
                                        }}
                                    >
                                        <td className="px-4 py-3">
                                            <div className="font-mono text-[12px] font-bold" style={{ color: "#22d3ee" }}>{task.id}</div>
                                            <div className="font-mono text-[10px]" style={{ color: "var(--text-dimmest)" }}>{task.created}</div>
                                        </td>
                                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{task.pickup}</td>
                                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{task.delivery}</td>
                                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-muted)" }}>{task.packageInfo}</td>
                                        <td className="px-4 py-3"><PriorityBadge priority={task.priority} /></td>
                                        <td className="px-4 py-3">
                                            {task.robotId ? (
                                                <span className="flex items-center gap-1.5 font-mono text-[12px]" style={{ color: "var(--text-secondary)" }}>
                                                    <Icon d={IC.robots} size={12} /> {task.robotId}
                                                </span>
                                            ) : task.status === "ASSIGNED" || task.status === "EXECUTING" ? (
                                                <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>Assigned (no details)</span>
                                            ) : (
                                                <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>—</span>
                                            )}
                                        </td>
                                        <td className="px-4 py-3"><StatusBadge status={task.status} /></td>
                                        <td className="px-4 py-3">
                                            <div className="flex items-center gap-1.5">
                                                {task.taskId != null && (
                                                    <button
                                                        type="button"
                                                        onClick={() => setDetailsTarget(task)}
                                                        aria-label={`View details for task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold bg-slate-400/10 text-slate-600 dark:text-slate-300 border border-slate-400/30 transition-all hover:bg-slate-400/20 hover:text-slate-700 dark:hover:text-slate-200 active:scale-[0.98] focus-visible:outline-2 focus-visible:outline-slate-400 focus-visible:outline-offset-2"
                                                    >
                                                        DETAILS
                                                    </button>
                                                )}
                                                {task.status === "QUEUED" && task.taskId != null && !task.robotId && (
                                                    <button type="button" onClick={() => setAssignTarget(task)} aria-label={`Assign robot to task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold bg-cyan-400/10 text-cyan-600 dark:text-cyan-300 border border-cyan-400/30 transition-all hover:bg-cyan-400/20 hover:text-cyan-700 dark:hover:text-cyan-200 active:scale-[0.98] focus-visible:outline-2 focus-visible:outline-cyan-400 focus-visible:outline-offset-2">
                                                        ASSIGN
                                                    </button>
                                                )}
                                                {canEdit(task.status) && (
                                                    <button
                                                        onClick={() => openEdit(task)}
                                                        aria-label={`Edit task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold bg-indigo-400/10 text-indigo-600 dark:text-indigo-300 border border-indigo-400/30 transition-all hover:bg-indigo-400/20 hover:text-indigo-700 dark:hover:text-indigo-200 active:scale-[0.98] focus-visible:outline-2 focus-visible:outline-indigo-400 focus-visible:outline-offset-2"
                                                    >
                                                        EDIT
                                                    </button>
                                                )}
                                                {canCancel(task.status) && (
                                                    <button
                                                        onClick={() => handleCancel(task)}
                                                        aria-label={`Cancel task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold bg-red-400/10 text-red-600 dark:text-red-300 border border-red-400/30 transition-all hover:bg-red-400/20 hover:text-red-700 dark:hover:text-red-200 active:scale-[0.98] focus-visible:outline-2 focus-visible:outline-red-400 focus-visible:outline-offset-2"
                                                    >
                                                        CANCEL
                                                    </button>
                                                )}
                                                {(task.status === "COMPLETED" || task.status === "CANCELLED") && (
                                                    <button
                                                        onClick={() => handleDelete(task)}
                                                        aria-label={`Delete task ${task.id}`}
                                                        className="w-7 h-7 rounded-md flex items-center justify-center transition-colors"
                                                        style={{
                                                            background: "rgba(239,68,68,0.1)",
                                                            color: "#f87171",
                                                            border: "1px solid rgba(239,68,68,0.3)",
                                                        }}
                                                    >
                                                        <Icon d={IC.trash} size={12} />
                                                    </button>
                                                )}
                                            </div>
                                        </td>
                                    </tr>
                                ))}
                                {section.items.length === 0 && (
                                    <tr>
                                        <td
                                            colSpan={8}
                                            className="text-center py-12 text-[13px]"
                                            style={{ color: "var(--text-dimmest)" }}
                                        >
                                            {isTasksLoading ? "Loading tasks..." : section.empty}
                                        </td>
                                    </tr>
                                )}
                            </tbody>
                        </table>
                    </div>
                    <div
                        className="flex justify-between px-4 py-2.5 shrink-0"
                        style={{ borderTop: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
                    >
                        <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>
                            Showing {section.items.length} of {section.total} {section.id === "completed" ? "completed" : "queue"} tasks
                        </span>
                        <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>
                            {robots.filter((r) => r.mqtt === "ONLINE").length} robots online
                        </span>
                    </div>
                </section>
                ))}
            </div>

            {/* ══ Create Task Modal ══ */}
            <ModalShell title="Create Transport Task" sub="Set the route, package details and scheduling preferences." open={createOpen} busy={isCreating} onClose={() => setCreateOpen(false)} width={680}>
                    <TaskCreateForm onBusyChange={setIsCreating} onCancel={() => setCreateOpen(false)} onCreated={(task) => {
                        setCreateOpen(false);
                        setToast({ msg: `Task ${task.id} created successfully.`, type: "success" });
                    }} />
                </ModalShell>

            {/* ══ Edit Task Modal ══ */}
            {editTarget && (
                <ModalShell title="Edit Transport Task" sub={`Modifying ${editTarget.id}`} onClose={() => setEditTarget(null)} width={500}>
                    <form onSubmit={(event) => { event.preventDefault(); confirmEdit(); }}>
                    <div
                        className="px-6 py-3 flex gap-6"
                        style={{ background: "rgba(99,102,241,0.06)", borderBottom: "1px solid rgba(99,102,241,0.15)" }}
                    >
                        {[
                            ["Task ID", editTarget.id, "#22d3ee"],
                            ["Status", editTarget.status, "#c084fc"],
                            ["Robot", editTarget.robotId || "Unassigned", "#94a3b8"],
                        ].map(([l, v, c]) => (
                            <div key={l}>
                                <div className="font-mono text-[9px] font-bold uppercase tracking-wider" style={{ color: "var(--text-faint)" }}>{l}</div>
                                <div className="font-mono text-[12px] font-bold mt-0.5" style={{ color: c }}>{v}</div>
                            </div>
                        ))}
                    </div>
                    <div className="p-6 space-y-5">
                        {editError && (
                            <div role="alert" className="rounded-lg px-3 py-2 text-[12px] bg-red-500/10 text-red-300 border border-red-500/20">
                                {editError}
                            </div>
                        )}
                        <div className="grid grid-cols-2 gap-4">
                            <ReadOnlyField label="Pickup Location" value={editTarget.pickup} />
                            <ReadOnlyField label="Package Info" value={editTarget.packageInfo} />
                        </div>
                        {editDetails?.deliveryStationName && (
                            <ReadOnlyField label="Current Delivery Name" value={editDetails.deliveryStationName} />
                        )}
                        <div className="grid grid-cols-3 gap-4">
                            <InputField
                                autoFocus
                                label="Warehouse ID"
                                value={editState.warehouseId}
                                onChange={(v) => setEditState((s) => ({ ...s, warehouseId: v.replace(/[^\d]/g, "") }))}
                                placeholder={editLoading ? "Loading..." : "e.g. 1"}
                                inputMode="numeric"
                            />
                            <InputField
                                label="Pickup Station ID"
                                value={editState.pickupStationId}
                                onChange={(v) => setEditState((s) => ({ ...s, pickupStationId: v.replace(/[^\d]/g, "") }))}
                                placeholder={editLoading ? "Loading..." : "e.g. 2"}
                                inputMode="numeric"
                            />
                            <InputField
                                label="Delivery Station ID"
                                value={editState.deliveryStationId}
                                onChange={(v) => setEditState((s) => ({ ...s, deliveryStationId: v.replace(/[^\d]/g, "") }))}
                                placeholder={editLoading ? "Loading..." : "e.g. 4"}
                                inputMode="numeric"
                            />
                        </div>
                        <div className="grid grid-cols-2 gap-4">
                            <InputField
                                label="Package Code"
                                value={editState.packageCode}
                                onChange={(v) => setEditState((s) => ({ ...s, packageCode: v }))}
                                placeholder="e.g. PKG-001"
                            />
                            <InputField
                                label="Payload Weight (kg)"
                                value={editState.payloadWeightKg}
                                onChange={(v) => setEditState((s) => ({ ...s, payloadWeightKg: v.replace(/[^\d.]/g, "") }))}
                                placeholder="e.g. 12.5"
                                inputMode="decimal"
                            />
                        </div>
                        <TextAreaField
                            label="Item Description"
                            value={editState.itemDescription}
                            onChange={(v) => setEditState((s) => ({ ...s, itemDescription: v }))}
                            placeholder="Describe the item..."
                        />
                        <InputField
                            label="Scheduled Time"
                            value={editState.scheduledTime}
                            onChange={(v) => setEditState((s) => ({ ...s, scheduledTime: v }))}
                            type="datetime-local"
                        />
                        <PriorityPicker
                            value={editState.priority}
                            onChange={(p) => setEditState((s) => ({ ...s, priority: p }))}
                            label="Priority Level ✎"
                        />
                    </div>
                    <div
                        className="flex gap-3 px-6 py-4"
                        style={{ borderTop: "1px solid var(--border-subtle)" }}
                    >
                        <button
                            type="button"
                            onClick={() => setEditTarget(null)}
                            disabled={editBusy}
                            className="flex-1 py-2.5 rounded-lg text-[13px] font-semibold"
                            style={{
                                background: "var(--surface-3)",
                                color: "var(--text-muted)",
                                border: "1px solid var(--border-medium)",
                            }}
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={editBusy || editLoading}
                            className="flex-[2] py-2.5 rounded-lg text-[13px] font-bold"
                            style={{
                                background: "linear-gradient(135deg,#22d3ee,#3b82f6)",
                                color: "#ffffff",
                                boxShadow: "0 4px 16px rgba(34,211,238,0.3)",
                            }}
                        >
                            {editBusy ? "Updating..." : editLoading ? "Loading..." : "✓ Confirm Update"}
                        </button>
                    </div>
                    </form>
                </ModalShell>
            )}

            {detailsTarget && (
                <ModalShell title="Task Details" sub={`Details for ${detailsTarget.id}`} onClose={() => { setDetailsTarget(null); setDetails(null); setDetailsError(""); }} width={640}>
                    <div className="p-6 space-y-4">
                        {detailsLoading && (
                            <div className="text-[13px]" style={{ color: "var(--text-muted)" }}>
                                Loading task details...
                            </div>
                        )}
                        {detailsError && (
                            <div role="alert" className="rounded-lg px-3 py-2 text-[12px] bg-red-500/10 text-red-300 border border-red-500/20">
                                {detailsError}
                            </div>
                        )}
                        {details && (
                            <div className="grid grid-cols-2 gap-4">
                                <ReadOnlyField label="Backend Task ID" value={details.taskId != null ? String(details.taskId) : "—"} />
                                <ReadOnlyField label="Tracking Code" value={details.taskTrackingCode ? String(details.taskTrackingCode) : "—"} />
                                <ReadOnlyField label="Warehouse ID" value={details.warehouseId != null ? String(details.warehouseId) : "—"} />
                                <ReadOnlyField label="Priority Level" value={formatPriorityLevel(details.priorityLevel)} />
                                <ReadOnlyField label="Pickup Station ID" value={details.pickupStationId != null ? String(details.pickupStationId) : "—"} />
                                <ReadOnlyField label="Delivery Station ID" value={details.deliveryStationId != null ? String(details.deliveryStationId) : "—"} />
                                <ReadOnlyField label="Pickup Name" value={details.pickupStationName ? String(details.pickupStationName) : "—"} />
                                <ReadOnlyField label="Delivery Name" value={details.deliveryStationName ? String(details.deliveryStationName) : "—"} />
                                <ReadOnlyField label="Package Code" value={details.packageCode ? String(details.packageCode) : "—"} />
                                <ReadOnlyField label="Item Description" value={details.itemDescription ? String(details.itemDescription) : "—"} />
                                <ReadOnlyField label="Payload (kg)" value={details.payloadWeightKg != null ? String(details.payloadWeightKg) : "—"} />
                                <ReadOnlyField label="Scheduled Time" value={details.scheduledTime ? String(details.scheduledTime) : "—"} />
                                <ReadOnlyField label="Created At" value={details.createdAt ? String(details.createdAt) : "—"} />
                                <ReadOnlyField label="Assigned Robot ID" value={details.assignedRobotId ? String(details.assignedRobotId) : details.robotId ? String(details.robotId) : "—"} />
                                <ReadOnlyField label="Task Status" value={details.taskStatus ? String(details.taskStatus) : "—"} />
                            </div>
                        )}
                    </div>
                    <div className="px-6 py-4" style={{ borderTop: "1px solid var(--border-subtle)" }}>
                        <button
                            type="button"
                            onClick={() => { setDetailsTarget(null); setDetails(null); setDetailsError(""); }}
                            className="w-full py-2.5 rounded-lg text-[13px] font-semibold"
                            style={{
                                background: "var(--surface-3)",
                                color: "var(--text-muted)",
                                border: "1px solid var(--border-medium)",
                            }}
                        >
                            Close
                        </button>
                    </div>
                </ModalShell>
            )}

            {/* ══ Delete Confirm ══ */}
            {deleteTarget && (
                <ModalShell title="Confirm Deletion" sub="This action cannot be undone." busy={isDeleting} onClose={() => { setDeleteTarget(null); setDeleteError(""); }} width={400}>
                    <div className="p-6 text-center">
                        <div
                            className="w-14 h-14 rounded-full flex items-center justify-center mx-auto mb-4"
                            style={{
                                background: "rgba(239,68,68,0.12)",
                                border: "1px solid rgba(239,68,68,0.3)",
                                color: "#f87171",
                            }}
                        >
                            <Icon d={IC.trash} size={24} />
                        </div>
                        <p className="text-[13px] leading-relaxed mb-6" style={{ color: "var(--text-muted)" }}>
                            You are about to permanently delete <br />
                            <strong style={{ color: "var(--text-primary)" }}>{deleteTarget.id}</strong>.
                        </p>
                        {deleteError && <p role="alert" className="mb-4 rounded-lg p-3 text-[12px] text-red-400 bg-red-500/10">{deleteError}</p>}
                        <div className="flex gap-3">
                            <button
                                autoFocus
                                type="button"
                                disabled={isDeleting}
                                onClick={() => { setDeleteTarget(null); setDeleteError(""); }}
                                className="flex-1 py-2.5 rounded-lg text-[13px] font-semibold"
                                style={{
                                    background: "var(--surface-3)",
                                    color: "var(--text-muted)",
                                    border: "1px solid var(--border-medium)",
                                }}
                            >
                                Cancel
                            </button>
                            <button
                                type="button"
                                disabled={isDeleting}
                                onClick={confirmDelete}
                                className="flex-1 py-2.5 rounded-lg text-[13px] font-bold"
                                style={{
                                    background: "rgba(239,68,68,0.15)",
                                    color: "#f87171",
                                    border: "1px solid rgba(239,68,68,0.4)",
                                }}
                            >
                                {isDeleting ? "Deleting..." : "Delete Permanently"}
                            </button>
                        </div>
                    </div>
                </ModalShell>
            )}

            {assignTarget && (
                <TaskAssignmentModal task={assignTarget} onClose={() => setAssignTarget(null)} onAssigned={async (result) => {
                    const display = result.robotCode || `R${result.robotId}`;
                    setTaskRobot(result.taskId, display);
                    setAssignTarget(null);
                    setToast({ msg: `Assigned ${display} successfully.`, type: "success" });
                }} />
            )}
            {/* ══ Toast ══ */}
            {toast && <ToastEl msg={toast.msg} type={toast.type} onDone={() => setToast(null)} />}
        </div>
    );
}

// ─── Reusable theme-aware form components ────────────────────────────────────

function TaskAssignmentModal({ task, onClose, onAssigned }: {
    task: Task; onClose: () => void; onAssigned: (result: ManualAssignTaskResponse) => Promise<void>;
}) {
    const [robots, setRobots] = useState<AvailableRobot[]>([]);
    const [loading, setLoading] = useState(true);
    const [busy, setBusy] = useState(false);
    const inFlight = useRef(false);
    const [error, setError] = useState("");
    const [selected, setSelected] = useState("");
    const [retry, setRetry] = useState(0);
    const id = useId();
    const eligibilityError = error.toLowerCase().includes("payload safety limit") || error.toLowerCase().includes("limit has not been configured");
    useEffect(() => {
        let active = true;
        setLoading(true);
        setError("");
        setSelected("");
        if (task.taskId == null) {
            setError("Missing backend task ID. Reload the task list.");
            setLoading(false);
            return;
        }
        getAvailableRobotsForTask(task.taskId).then(items => {
            if (active) setRobots(items);
        }).catch(err => {
            if (active) setError(err?.message || "Unable to load available robots.");
        }).finally(() => { if (active) setLoading(false); });
        return () => { active = false; };
    }, [task.taskId, retry]);

    return (
        <ModalShell title={`Assign Robot / ${task.id}`} sub={`${task.pickup} to ${task.delivery}`} onClose={onClose} busy={busy} width={540}>
            <form className="p-6 space-y-4" aria-busy={loading || busy} onSubmit={async event => {
                event.preventDefault();
                const robotId = Number(selected);
                if (inFlight.current || task.taskId == null || !robots.some(robot => robot.robotId === robotId)) return;
                inFlight.current = true;
                setBusy(true);
                setError("");
                try {
                    const result = await assignRobotToTask(task.taskId, robotId);
                    inFlight.current = false;
                    setBusy(false);
                    await onAssigned(result);
                    return;
                } catch (err) {
                    setError((err as { message?: string })?.message || "Assignment failed. Reload available robots and try again.");
                    inFlight.current = false;
                    setBusy(false);
                    return;
                }
            }}>
                <p className="text-[12px]" style={{ color: "var(--text-faint)" }}>Only robots eligible for this task are shown. Confirming sends an assignment to the backend.</p>
                {error && (
                    <div
                        role="alert"
                        className="rounded-lg px-3 py-2 text-[12px]"
                        style={{
                            background: "rgba(239,68,68,0.12)",
                            border: "1px solid rgba(239,68,68,0.35)",
                            color: "#f87171",
                        }}
                    >
                        {eligibilityError
                            ? "Task không thể assign: vượt quá giới hạn tải trọng an toàn đã xác minh hoặc hệ thống chưa cấu hình giới hạn. Hãy kiểm tra payload/setting robot."
                            : error}
                    </div>
                )}
                {loading ? <p role="status">Loading available robots...</p> : !error ? (
                    <div>
                        <label htmlFor={id} className="block text-[12px] mb-2">Available Robot</label>
                        <select id={id} autoFocus required value={selected} disabled={busy} onChange={event => setSelected(event.target.value)}
                            className="w-full rounded-lg p-3 text-[13px] focus-visible:outline-2 focus-visible:outline-cyan-400"
                            style={{ background: "var(--surface-3)", color: "var(--text-primary)", border: "1px solid var(--border-medium)" }}>
                            <option value="">Select a robot</option>
                            {robots.filter(robot => robot.robotId != null).map(robot => (
                                <option key={robot.robotId} value={robot.robotId!}>
                                    {robot.id} | Battery: {robot.battery == null ? "N/A" : `${robot.battery}%`} | {robot.mqtt || "Unknown connection"} | {robot.operationalStatus || "Unknown status"}
                                </option>
                            ))}
                        </select>
                        {!robots.length && <p className="mt-2 text-[12px]">No available robots for this task.</p>}
                    </div>
                ) : null}
                <div className="flex flex-wrap gap-3">
                    <button type="button" disabled={busy || loading} onClick={() => setRetry(value => value + 1)} className="rounded-lg border px-3 py-2 disabled:opacity-50">Reload Robots</button>
                    <button type="button" disabled={busy} onClick={onClose} className="rounded-lg border px-3 py-2 disabled:opacity-50">Cancel</button>
                    <button type="submit" disabled={busy || loading || !!error || !selected} className="rounded-lg bg-cyan-400 text-slate-950 font-bold px-3 py-2 disabled:opacity-50">
                        {busy ? "Assigning..." : "Confirm Assignment"}
                    </button>
                </div>
            </form>
        </ModalShell>
    );
}

function ModalShell({
    title, sub, onClose, children, width = 480, open = true, busy = false,
}: { title: string; sub?: string; onClose: () => void; children: React.ReactNode; width?: number; open?: boolean; busy?: boolean }) {
    const dialogRef = useRef<HTMLDialogElement>(null);
    const titleId = useId();
    const descriptionId = useId();

    useEffect(() => {
        const dialog = dialogRef.current;
        if (!dialog || !open) return;
        const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        // Native modal dialogs trap focus and make the rest of the page inert.
        dialog.showModal();
        dialog.querySelector<HTMLElement>("[autofocus]")?.focus();
        return () => {
            dialog.close();
            if (trigger?.isConnected) trigger.focus();
        };
    }, [open]);

    return (
        <dialog
            ref={dialogRef}
            role="dialog"
            aria-modal="true"
            aria-labelledby={titleId}
            aria-describedby={sub ? descriptionId : undefined}
            onCancel={(event) => { event.preventDefault(); if (!busy) onClose(); }}
            className="m-auto rounded-2xl p-0 max-h-[90vh] overflow-auto backdrop:bg-black/75 backdrop:backdrop-blur-sm"
            style={{ width: `min(${width}px, calc(100vw - 32px))`, color: "var(--text-primary)", background: "var(--surface-2)", border: "1px solid var(--border-medium)" }}
        >
            <div
                className="w-full rounded-2xl overflow-hidden"
                style={{
                    maxWidth: width,
                    background: "var(--surface-2)",
                    border: "1px solid var(--border-medium)",
                    boxShadow: "0 32px 80px rgba(0,0,0,0.6)",
                }}
                onClick={(e) => e.stopPropagation()}
            >
                <div
                    className="flex items-start justify-between px-6 py-5"
                    style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
                >
                    <div>
                        <h2 id={titleId} className="text-[15px] font-bold" style={{ color: "var(--text-primary)" }}>{title}</h2>
                        {sub && <p id={descriptionId} className="font-mono text-[11px] mt-0.5" style={{ color: "var(--text-faint)" }}>{sub}</p>}
                    </div>
                    <button
                        type="button"
                        aria-label="Close"
                        disabled={busy}
                        onClick={onClose}
                        className="w-7 h-7 rounded-lg flex items-center justify-center transition-colors"
                        style={{
                            background: "var(--surface-3)",
                            color: "var(--text-muted)",
                            border: "1px solid var(--border-medium)",
                        }}
                    >
                        <Icon d={IC.x} size={14} />
                    </button>
                </div>
                {children}
            </div>
        </dialog>
    );
}

function InputField({
    label, value, onChange, placeholder, autoFocus = false, type = "text", inputMode,
}: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; autoFocus?: boolean; type?: React.HTMLInputTypeAttribute; inputMode?: React.HTMLAttributes<HTMLInputElement>["inputMode"] }) {
    const id = useId();
    return (
        <div>
            <label
                htmlFor={id}
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
            <input
                id={id}
                autoFocus={autoFocus}
                type={type}
                inputMode={inputMode}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                placeholder={placeholder}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] focus-visible:outline-2 focus-visible:outline-cyan-400 transition-colors"
                style={{
                    background: "var(--surface-3)",
                    border: "1px solid var(--border-medium)",
                    color: "var(--text-primary)",
                }}
            />
        </div>
    );
}

function TextAreaField({
    label, value, onChange, placeholder,
}: { label: string; value: string; onChange: (v: string) => void; placeholder?: string }) {
    const id = useId();
    return (
        <div>
            <label
                htmlFor={id}
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
            <textarea
                id={id}
                value={value}
                onChange={(e) => onChange(e.target.value)}
                placeholder={placeholder}
                rows={3}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] focus-visible:outline-2 focus-visible:outline-cyan-400 transition-colors resize-none"
                style={{
                    background: "var(--surface-3)",
                    border: "1px solid var(--border-medium)",
                    color: "var(--text-primary)",
                }}
            />
        </div>
    );
}

function ReadOnlyField({ label, value }: { label: string; value: string }) {
    return (
        <div>
            <p
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </p>
            <div
                className="px-3 py-2.5 rounded-lg text-[13px]"
                style={{
                    background: "var(--surface-3)",
                    border: "1px dashed var(--border-medium)",
                    color: "var(--text-muted)",
                }}
            >
                {value}
            </div>
        </div>
    );
}

function PriorityPicker({
    value, onChange, label,
}: { value: Priority; onChange: (p: Priority) => void; label: string }) {
    const colors: Record<Priority, string> = { HIGH: "#f87171", MEDIUM: "#fbbf24", LOW: "#94a3b8" };
    const name = useId();
    return (
        <fieldset>
            <legend
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-2"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </legend>
            <div className="flex gap-2.5">
                {(["HIGH", "MEDIUM", "LOW"] as Priority[]).map((p) => {
                    const selected = value === p;
                    const color = colors[p];
                    return (
                        <label
                            key={p}
                            className="flex-1 flex items-center justify-center gap-2 py-2.5 rounded-lg text-[11px] font-mono font-bold transition-all cursor-pointer"
                            style={{
                                background: selected ? `${color}20` : "var(--surface-3)",
                                border: `2px solid ${selected ? color : "var(--border-medium)"}`,
                                color: selected ? color : "var(--text-faint)",
                            }}
                        >
                            <input type="radio" name={name} value={p} checked={selected} onChange={() => onChange(p)} className="accent-cyan-400" />
                            {p}
                        </label>
                    );
                })}
            </div>
        </fieldset>
    );
}

function ToastEl({
    msg, type, onDone,
}: { msg: string; type: "success" | "error" | "info"; onDone: () => void }) {
    const [visible, setVisible] = useState(true);
    useEffect(() => {
        const t = setTimeout(() => { setVisible(false); setTimeout(onDone, 200); }, 3000);
        return () => clearTimeout(t);
    }, [onDone]);

    const colors = {
        success: { bg: "rgba(34,197,94,0.12)", border: "rgba(34,197,94,0.4)", color: "#22c55e", icon: IC.check },
        error: { bg: "rgba(239,68,68,0.12)", border: "rgba(239,68,68,0.4)", color: "#ef4444", icon: IC.x },
        info: { bg: "rgba(99,102,241,0.12)", border: "rgba(99,102,241,0.4)", color: "#818cf8", icon: IC.info },
    };
    const c = colors[type];

    return (
        <div
            className="fixed bottom-6 right-6 z-[200] flex items-center gap-2.5 px-4 py-3 rounded-xl transition-all"
            style={{
                background: "var(--surface-2)",
                border: `1px solid ${c.border}`,
                boxShadow: "0 8px 32px rgba(0,0,0,0.4)",
                opacity: visible ? 1 : 0,
                transform: visible ? "translateY(0)" : "translateY(20px)",
                minWidth: 280,
            }}
        >
            <span
                className="w-7 h-7 rounded-lg flex items-center justify-center shrink-0"
                style={{ background: c.bg, color: c.color }}
            >
                <Icon d={c.icon} size={14} />
            </span>
            <span className="text-[13px]" style={{ color: "var(--text-primary)" }}>{msg}</span>
        </div>
    );
}
