import { useState, useEffect, useId, useRef } from "react";
import { TaskCreateForm } from "./TaskCreationPage";
import { useData } from "../context/DataContext";
import { Priority, Task } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn, PriorityBadge, StatusBadge } from "../components/SharedUI";

// ══════════════════════════════════════════════════════════════════════════════
// Tasks Page — Full lifecycle management (list + create + edit + cancel + delete)
// ═══════════════════════════════════════════════════════════════════════════════

interface EditState { delivery: string; priority: Priority; }

const PRIORITY_ORDER: Record<Priority, number> = { HIGH: 0, MEDIUM: 1, LOW: 2 };

export function TasksPage() {
    const { tasks, robots, cancelTask, deleteTask, refreshTasks, isTasksLoading, tasksError } = useData();
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<string>("ALL");
    const [createOpen, setCreateOpen] = useState(false);
    const [editTarget, setEditTarget] = useState<Task | null>(null);
    const [editState, setEditState] = useState<EditState>({ delivery: "", priority: "MEDIUM" });
    const [deleteTarget, setDeleteTarget] = useState<Task | null>(null);
    const [isDeleting, setIsDeleting] = useState(false);
    const deleteInFlight = useRef(false);
    const [deleteError, setDeleteError] = useState("");
    const [toast, setToast] = useState<{ msg: string; type: "success" | "error" | "info" } | null>(null);

    const [isCreating, setIsCreating] = useState(false);

    useEffect(() => {
        void refreshTasks().catch(() => {});

        const handleWindowFocus = () => {
            void refreshTasks().catch(() => {});
        };

        const handleVisibilityChange = () => {
            if (document.visibilityState === "visible") {
                void refreshTasks().catch(() => {});
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
        setEditState({ delivery: task.delivery, priority: task.priority });
    };

    const confirmEdit = () => {
        if (!editTarget) return;
        setToast({ msg: "Editing is not connected to the backend yet. No changes were saved.", type: "info" });
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
                                            ) : (
                                                <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>—</span>
                                            )}
                                        </td>
                                        <td className="px-4 py-3"><StatusBadge status={task.status} /></td>
                                        <td className="px-4 py-3">
                                            <div className="flex items-center gap-1.5">
                                                {canEdit(task.status) && (
                                                    <button
                                                        onClick={() => openEdit(task)}
                                                        aria-label={`Edit task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold transition-colors"
                                                        style={{
                                                            background: "rgba(99,102,241,0.1)",
                                                            color: "#818cf8",
                                                            border: "1px solid rgba(99,102,241,0.3)",
                                                        }}
                                                    >
                                                        EDIT
                                                    </button>
                                                )}
                                                {canCancel(task.status) && (
                                                    <button
                                                        onClick={() => handleCancel(task)}
                                                        aria-label={`Cancel task ${task.id}`}
                                                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold transition-colors"
                                                        style={{
                                                            background: "rgba(239,68,68,0.1)",
                                                            color: "#f87171",
                                                            border: "1px solid rgba(239,68,68,0.3)",
                                                        }}
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
                        <div className="grid grid-cols-2 gap-4">
                            <ReadOnlyField label="Pickup Location" value={editTarget.pickup} />
                            <ReadOnlyField label="Package Info" value={editTarget.packageInfo} />
                        </div>
                        <InputField
                            autoFocus
                            label="Delivery Station / Destination"
                            value={editState.delivery}
                            onChange={(v) => setEditState((s) => ({ ...s, delivery: v }))}
                            placeholder="e.g. Delivery Station D1"
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
                            className="flex-[2] py-2.5 rounded-lg text-[13px] font-bold"
                            style={{
                                background: "linear-gradient(135deg,#22d3ee,#3b82f6)",
                                color: "#ffffff",
                                boxShadow: "0 4px 16px rgba(34,211,238,0.3)",
                            }}
                        >
                            ✓ Confirm Update
                        </button>
                    </div>
                    </form>
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

            {/* ══ Toast ══ */}
            {toast && <ToastEl msg={toast.msg} type={toast.type} onDone={() => setToast(null)} />}
        </div>
    );
}

// ─── Reusable theme-aware form components ────────────────────────────────────

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
    label, value, onChange, placeholder, autoFocus = false,
}: { label: string; value: string; onChange: (v: string) => void; placeholder?: string; autoFocus?: boolean }) {
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
                type="text"
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
