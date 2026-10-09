import { useId, useRef, useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useData } from "../context/DataContext";
import type { Task } from "../data/mockData";

export function TaskCreateForm({
    onCreated, onCancel, onBusyChange,
}: {
    onCreated: (task: Task) => void;
    onCancel: () => void;
    onBusyChange?: (busy: boolean) => void;
}) {
    const { addTask } = useData();
    const id = useId();
    const submitting = useRef(false);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const inputClass = "w-full rounded-lg px-3 py-2.5 text-[13px] focus-visible:outline-2 focus-visible:outline-cyan-400";
    const inputStyle = { background: "var(--surface-3)", color: "var(--text-primary)", border: "1px solid var(--border-medium)" };

    async function submit(event: FormEvent<HTMLFormElement>) {
        event.preventDefault();
        if (submitting.current) return;
        const form = event.currentTarget;
        const values = new FormData(form);
        const warehouseId = Number(values.get("warehouseId"));
        const pickupStationId = Number(values.get("pickupStationId"));
        const deliveryStationId = Number(values.get("deliveryStationId"));
        if (![warehouseId, pickupStationId, deliveryStationId].every(value => Number.isInteger(value) && value > 0 && value <= 2147483647)) {
            setError("Warehouse and station IDs must be positive integers.");
            return;
        }
        if (pickupStationId === deliveryStationId) {
            setError("Pickup and delivery stations must be different.");
            return;
        }
        submitting.current = true;
        setBusy(true);
        onBusyChange?.(true);
        setError("");
        let created: Task | undefined;
        try {
            const scheduledTime = String(values.get("scheduledTime") || "");
            const weight = String(values.get("payloadWeightKg") || "");
            created = await addTask({
                warehouseId, pickupStationId, deliveryStationId,
                priorityLevel: Number(values.get("priorityLevel")),
                packageCode: String(values.get("packageCode") || "").trim() || null,
                itemDescription: String(values.get("itemDescription") || "").trim() || null,
                payloadWeightKg: weight ? Number(weight) : null,
                scheduledTime: scheduledTime ? new Date(scheduledTime).toISOString() : null,
            });
        } catch (err) {
            const apiError = err as { message?: string; errors?: Record<string, string[]> };
            setError([apiError.message || "Unable to create task.", ...Object.values(apiError.errors || {}).flat()].join(" "));
        } finally {
            submitting.current = false;
            setBusy(false);
            onBusyChange?.(false);
        }
        if (created) {
            form.reset();
            onCreated(created);
        }
    }

    return (
        <form onSubmit={submit} className="p-6 space-y-5" aria-busy={busy}>
            <div id={`${id}-help`} className="rounded-xl px-4 py-3 text-[12px] leading-relaxed bg-cyan-400/10 text-cyan-600 dark:text-cyan-300">
                Use existing warehouse and station IDs. Task tracking codes are generated automatically.
                Required fields are marked *. Station selection by name will be available when connected to a station API.
            </div>
            <fieldset disabled={busy} className="space-y-5 disabled:opacity-60">
                <fieldset className="rounded-xl p-4 space-y-4" style={{ border: "1px solid var(--border-subtle)" }}>
                    <legend className="px-2 text-[13px] font-bold">01 / Transport Route</legend>
                    <div>
                        <label htmlFor={`${id}-warehouseId`} className="block text-[12px] mb-1.5">Warehouse ID *</label>
                        <input id={`${id}-warehouseId`} name="warehouseId" type="number" min={1} max={2147483647} step={1} required
                            aria-describedby={`${id}-help`} autoFocus placeholder="Enter warehouse ID" className={inputClass} style={inputStyle} />
                    </div>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        {[
                            ["pickupStationId", "Pickup Station ID", "Where the robot collects the package"],
                            ["deliveryStationId", "Delivery Station ID", "Where the robot delivers the package"],
                        ].map(([name, label, hint]) => (
                            <div key={name}>
                                <label htmlFor={`${id}-${name}`} className="block text-[12px] mb-1.5">{label} *</label>
                                <input id={`${id}-${name}`} name={name} type="number" min={1} max={2147483647} step={1} required
                                    aria-describedby={`${id}-${name}-hint`} placeholder="Enter station ID" className={inputClass} style={inputStyle} />
                                <p id={`${id}-${name}-hint`} className="mt-1.5 text-[11px]" style={{ color: "var(--text-faint)" }}>{hint}</p>
                            </div>
                        ))}
                    </div>
                </fieldset>
                <fieldset className="rounded-xl p-4 space-y-4" style={{ border: "1px solid var(--border-subtle)" }}>
                    <legend className="px-2 text-[13px] font-bold">02 / Package Details <span className="font-normal text-[11px]">(optional)</span></legend>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <div>
                    <label htmlFor={`${id}-package`} className="block text-[12px] mb-1.5">Package Code (optional)</label>
                    <input id={`${id}-package`} name="packageCode" maxLength={80} placeholder="e.g. PKG-1001" className={inputClass} style={inputStyle} />
                </div>
                    <div>
                        <label htmlFor={`${id}-weight`} className="block text-[12px] mb-1.5">Weight (kg, optional)</label>
                        <input id={`${id}-weight`} name="payloadWeightKg" type="number" min={0.01} max={999999.99} step={0.01}
                            placeholder="e.g. 5.00" className={inputClass} style={inputStyle} />
                    </div>
                    </div>
                <div>
                    <label htmlFor={`${id}-description`} className="block text-[12px] mb-1.5">Item Description (optional)</label>
                    <textarea id={`${id}-description`} name="itemDescription" maxLength={500} rows={2}
                        placeholder="Describe the package or handling instructions..." className={`${inputClass} resize-y`} style={inputStyle} />
                </div>
                </fieldset>
                <fieldset className="rounded-xl p-4 space-y-4" style={{ border: "1px solid var(--border-subtle)" }}>
                    <legend className="px-2 text-[13px] font-bold">03 / Priority &amp; Schedule</legend>
                <fieldset>
                    <legend className="text-[12px] mb-2">Priority Level *</legend>
                    <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                        {[["1", "HIGH", "Urgent"], ["2", "MEDIUM", "Normal"], ["3", "LOW", "Flexible"]].map(([value, label, hint]) => (
                            <label key={value} className="relative flex items-center gap-3 rounded-xl border border-slate-500/30 p-3 text-[12px] cursor-pointer has-[:checked]:border-cyan-400 has-[:checked]:bg-cyan-400/10 focus-within:outline-2 focus-within:outline-cyan-400">
                                <input type="radio" name="priorityLevel" value={value} defaultChecked={value === "2"} required className="accent-cyan-400 shrink-0" />
                                <span><span className="block font-mono font-bold">{label}</span><span className="block mt-1 text-[11px]" style={{ color: "var(--text-faint)" }}>{hint}</span></span>
                            </label>
                        ))}
                    </div>
                </fieldset>
                    <div>
                        <label htmlFor={`${id}-schedule`} className="block text-[12px] mb-1.5">Scheduled Time (optional)</label>
                        <input id={`${id}-schedule`} name="scheduledTime" type="datetime-local" aria-describedby={`${id}-schedule-hint`} className={inputClass} style={inputStyle} />
                        <p id={`${id}-schedule-hint`} className="mt-1.5 text-[11px]" style={{ color: "var(--text-faint)" }}>Uses your local timezone. Leave blank for no scheduled time.</p>
                    </div>
                </fieldset>
            </fieldset>
            {error && <p role="alert" className="rounded-lg p-3 text-[12px] text-red-400 bg-red-500/10">{error}</p>}
            <div className="sticky bottom-0 flex gap-3 pt-4 pb-1" style={{ background: "var(--surface-2)", borderTop: "1px solid var(--border-subtle)" }}>
                <button type="button" disabled={busy} onClick={onCancel} className="flex-1 rounded-xl py-3 text-[13px] font-semibold disabled:opacity-50" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)" }}>Cancel</button>
                <button type="submit" disabled={busy} className="flex-[2] rounded-xl py-3 text-[13px] font-bold bg-cyan-400 text-slate-950 disabled:opacity-50">
                    {busy ? "Creating..." : "Create Task"}
                </button>
            </div>
        </form>
    );
}

export function TaskCreationPage() {
    const navigate = useNavigate();
    return (
        <main className="h-full overflow-auto p-6" style={{ background: "var(--background)", color: "var(--text-primary)" }}>
            <section className="max-w-2xl mx-auto rounded-2xl" style={{ background: "var(--surface-2)" }}>
                <h1 className="px-6 pt-6 font-bold text-lg">Create Transport Task</h1>
                <TaskCreateForm onCreated={() => navigate("/dashboard/tasks")} onCancel={() => navigate("/dashboard/tasks")} />
            </section>
        </main>
    );
}

export default TaskCreationPage;
