import { useState, useEffect } from "react";
import { useData } from "../context/DataContext";
import { Priority, Task } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn, PriorityBadge, StatusBadge } from "../components/SharedUI";

// ══════════════════════════════════════════════════════════════════════════════
// Tasks Page — Full lifecycle management (list + create + edit + cancel + delete)
// ═══════════════════════════════════════════════════════════════════════════════

interface EditState { delivery: string; priority: Priority; }

export function TasksPage() {
    const { tasks, robots, addTask, updateTaskStatus, cancelTask, refreshTasks, isTasksLoading, tasksError } = useData();
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<string>("ALL");
    const [createOpen, setCreateOpen] = useState(false);
    const [editTarget, setEditTarget] = useState<Task | null>(null);
    const [editState, setEditState] = useState<EditState>({ delivery: "", priority: "MEDIUM" });
    const [deleteTarget, setDeleteTarget] = useState<Task | null>(null);
    const [toast, setToast] = useState<{ msg: string; type: "success" | "error" | "info" } | null>(null);

    // Create form state
    const PICKUP_OPTIONS = ["Station A-01", "Station A-02", "Station A-03", "Station B-02", "Station B-03", "Station C-01", "Receiving Dock"];
    const DELIVERY_OPTIONS = ["Bay A-04", "Bay B-07", "Bay C-08", "Bay C-11", "Bay D-12", "Bay E-01", "Storage F-03", "Shipping Zone", "Receiving Dock"];
    const [newPickup, setNewPickup] = useState(PICKUP_OPTIONS[0]);
    const [newDelivery, setNewDelivery] = useState(DELIVERY_OPTIONS[0]);
    const [newPkg, setNewPkg] = useState("");
    const [newPrio, setNewPrio] = useState<Priority>("MEDIUM");

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
    const filtered = tasks.filter((t) => {
        const q = search.toLowerCase();
        const matchSearch = !q
            || t.id.toLowerCase().includes(q)
            || t.pickup.toLowerCase().includes(q)
            || t.delivery.toLowerCase().includes(q)
            || (t.robotId || "").toLowerCase().includes(q);
        const matchStatus = statusFilter === "ALL" || t.status === statusFilter;
        return matchSearch && matchStatus;
    });

    const statusOptions = ["ALL", "PENDING", "QUEUED", "ASSIGNED", "EXECUTING", "COMPLETED", "CANCELLED"];
    const counts = {
        total: tasks.length,
        pending: tasks.filter((t) => t.status === "PENDING" || t.status === "QUEUED").length,
        executing: tasks.filter((t) => t.status === "EXECUTING" || t.status === "ASSIGNED").length,
        completed: tasks.filter((t) => t.status === "COMPLETED").length,
    };

    // ── Actions ──
    const handleCreate = () => {
        if (!newPkg.trim()) { setToast({ msg: "Package description is required.", type: "error" }); return; }
        addTask(newPickup, newDelivery, newPkg, newPrio);
        setCreateOpen(false);
        setNewPkg("");
        setToast({ msg: `Task created: ${newPickup} → ${newDelivery}`, type: "success" });
    };

    const openEdit = (task: Task) => {
        setEditTarget(task);
        setEditState({ delivery: task.delivery, priority: task.priority });
    };

    const confirmEdit = () => {
        if (!editTarget) return;
        // In a real app this would call a service. For mock, we just close.
        setEditTarget(null);
        setToast({ msg: `${editTarget.id} updated.`, type: "success" });
    };

    const handleCancel = (task: Task) => {
        cancelTask(task.id);
        setToast({ msg: `${task.id} cancelled.`, type: "info" });
    };

    const handleDelete = (task: Task) => {
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

    const confirmDelete = () => {
        if (!deleteTarget) return;
        // In a real app this would call a delete service.
        setToast({ msg: `${deleteTarget.id} deleted permanently.`, type: "error" });
        setDeleteTarget(null);
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
                className="shrink-0 flex gap-3 px-6 py-3"
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
                className="shrink-0 flex items-center gap-3 px-6 py-3"
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
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder="Search task ID, station, robot…"
                        className="w-full pl-9 pr-3 py-2 rounded-lg text-[13px] outline-none transition-colors"
                        style={{
                            background: "var(--surface-3)",
                            border: "1px solid var(--border-medium)",
                            color: "var(--text-primary)",
                        }}
                    />
                </div>
                <div className="flex gap-1">
                    {statusOptions.map((s) => (
                        <button
                            key={s}
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

            {/* Table */}
            <div className="flex-1 overflow-hidden px-6 pb-6 pt-3">
                <div
                    className="h-full rounded-2xl overflow-hidden flex flex-col transition-colors"
                    style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                >
                    <div className="overflow-auto flex-1">
                        <table className="w-full border-collapse" style={{ minWidth: 1000 }}>
                            <thead>
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
                                {filtered.map((task) => (
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
                                {filtered.length === 0 && (
                                    <tr>
                                        <td
                                            colSpan={8}
                                            className="text-center py-12 text-[13px]"
                                            style={{ color: "var(--text-dimmest)" }}
                                        >
                                            No tasks match your filters.
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
                            Showing {filtered.length} of {tasks.length} tasks
                        </span>
                        <span className="font-mono text-[11px]" style={{ color: "var(--text-dimmest)" }}>
                            {robots.filter((r) => r.mqtt === "ONLINE").length} robots online
                        </span>
                    </div>
                </div>
            </div>

            {/* ══ Create Task Modal ══ */}
            {createOpen && (
                <ModalShell title="Create Transport Task" sub="New task will be queued immediately after submission." onClose={() => setCreateOpen(false)} width={500}>
                    <div className="p-6 space-y-5">
                        <SelectField label="Pickup Location" value={newPickup} onChange={setNewPickup} options={PICKUP_OPTIONS} />
                        <SelectField label="Delivery Destination" value={newDelivery} onChange={setNewDelivery} options={DELIVERY_OPTIONS} />
                        <InputField
                            label="Package Description"
                            value={newPkg}
                            onChange={setNewPkg}
                            placeholder="e.g. Industrial Parts × 3"
                        />
                        <PriorityPicker value={newPrio} onChange={setNewPrio} label="Priority Level" />
                    </div>
                    <div
                        className="flex gap-3 px-6 py-4"
                        style={{ borderTop: "1px solid var(--border-subtle)" }}
                    >
                        <button
                            onClick={() => setCreateOpen(false)}
                            className="flex-1 py-2.5 rounded-lg text-[13px] font-semibold transition-colors"
                            style={{
                                background: "var(--surface-3)",
                                color: "var(--text-muted)",
                                border: "1px solid var(--border-medium)",
                            }}
                        >
                            Cancel
                        </button>
                        <button
                            onClick={handleCreate}
                            className="flex-[2] py-2.5 rounded-lg text-[13px] font-bold transition-opacity hover:opacity-90"
                            style={{
                                background: "linear-gradient(135deg,#22d3ee,#3b82f6)",
                                color: "#ffffff",
                                boxShadow: "0 4px 16px rgba(34,211,238,0.3)",
                            }}
                        >
                            + Create Task
                        </button>
                    </div>
                </ModalShell>
            )}

            {/* ══ Edit Task Modal ══ */}
            {editTarget && (
                <ModalShell title="Edit Transport Task" sub={`Modifying ${editTarget.id}`} onClose={() => setEditTarget(null)} width={500}>
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
                            onClick={confirmEdit}
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
                </ModalShell>
            )}

            {/* ══ Delete Confirm ══ */}
            {deleteTarget && (
                <ModalShell title="Confirm Deletion" sub="This action cannot be undone." onClose={() => setDeleteTarget(null)} width={400}>
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
                        <div className="flex gap-3">
                            <button
                                onClick={() => setDeleteTarget(null)}
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
                                onClick={confirmDelete}
                                className="flex-1 py-2.5 rounded-lg text-[13px] font-bold"
                                style={{
                                    background: "rgba(239,68,68,0.15)",
                                    color: "#f87171",
                                    border: "1px solid rgba(239,68,68,0.4)",
                                }}
                            >
                                Delete Permanently
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
    title, sub, onClose, children, width = 480,
}: { title: string; sub?: string; onClose: () => void; children: React.ReactNode; width?: number }) {
    return (
        <div
            className="fixed inset-0 flex items-center justify-center z-[100] p-6"
            style={{ background: "rgba(0,0,0,0.75)", backdropFilter: "blur(6px)" }}
            onClick={onClose}
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
                        <h2 className="text-[15px] font-bold" style={{ color: "var(--text-primary)" }}>{title}</h2>
                        {sub && <p className="font-mono text-[11px] mt-0.5" style={{ color: "var(--text-faint)" }}>{sub}</p>}
                    </div>
                    <button
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
        </div>
    );
}

function SelectField({
    label, value, onChange, options,
}: { label: string; value: string; onChange: (v: string) => void; options: string[] }) {
    return (
        <div>
            <label
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
            <select
                value={value}
                onChange={(e) => onChange(e.target.value)}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] outline-none appearance-none cursor-pointer transition-colors"
                style={{
                    background: "var(--surface-3)",
                    border: "1px solid var(--border-medium)",
                    color: "var(--text-primary)",
                }}
            >
                {options.map((o) => (
                    <option key={o} value={o} style={{ background: "var(--surface-2)" }}>{o}</option>
                ))}
            </select>
        </div>
    );
}

function InputField({
    label, value, onChange, placeholder,
}: { label: string; value: string; onChange: (v: string) => void; placeholder?: string }) {
    return (
        <div>
            <label
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
            <input
                type="text"
                value={value}
                onChange={(e) => onChange(e.target.value)}
                placeholder={placeholder}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] outline-none transition-colors"
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
            <label
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
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
    return (
        <div>
            <label
                className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-2"
                style={{ color: "var(--text-faint)" }}
            >
                {label}
            </label>
            <div className="flex gap-2.5">
                {(["HIGH", "MEDIUM", "LOW"] as Priority[]).map((p) => {
                    const selected = value === p;
                    const color = colors[p];
                    return (
                        <button
                            key={p}
                            type="button"
                            onClick={() => onChange(p)}
                            className="flex-1 py-2.5 rounded-lg text-[11px] font-mono font-bold transition-all"
                            style={{
                                background: selected ? `${color}20` : "var(--surface-3)",
                                border: `2px solid ${selected ? color : "var(--border-medium)"}`,
                                color: selected ? color : "var(--text-faint)",
                            }}
                        >
                            {p}
                        </button>
                    );
                })}
            </div>
        </div>
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
