import {
    useId,
    useEffect,
    useRef,
    useState,
    type FormEvent,
} from "react";

import { useNavigate } from "react-router-dom";
import { useData } from "../context/DataContext";
import type { Task } from "../data/mockData";
import type { DispatchMode } from "../api/modules/tasks";
import { apiClient } from "../api/client";

// =====================================================
// CREATE TASK FORM WITH AUTO / MANUAL MODE
// =====================================================

export function TaskCreateForm({
    onCreated,
    onCancel,
    onBusyChange,
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

    // SAFE DEFAULT: MANUAL
    const [dispatchMode, setDispatchMode] =
        useState<DispatchMode>("MANUAL");
    const [qrPilotEnabled, setQrPilotEnabled] = useState(false);
    const [qrPoints, setQrPoints] = useState<Array<{ id: number; label: string }>>([]);
    useEffect(() => {
        let cancelled = false;
        async function loadQrPilot() {
            try {
                const health = await apiClient.get<{ enabled: boolean }>("/api/v1/demo-routing/health");
                if (!health.data.enabled) return;
                if (!cancelled) setQrPilotEnabled(true);
                const result = await apiClient.get<{ cells: Array<Array<{
                    type: string; label?: string; arucoId?: number | null
                }>> }>("/api/v1/navigation/map");
                const qrs = result.data.cells.flat()
                    .filter(c => c.type === "qr" && c.arucoId !== undefined && c.arucoId !== null)
                    .map(c => ({ id: Number(c.arucoId), label: c.label ?? `QR${c.arucoId}` }))
                    .sort((a, b) => a.id - b.id);
                if (!cancelled) setQrPoints(qrs);
            } catch (error) {
                console.warn("Demo QR route map not available", error);
            }
        }
        void loadQrPilot();
        return () => { cancelled = true; };
    }, []);

    const inputClass =
        "w-full rounded-lg px-3 py-2.5 text-[13px] " +
        "focus-visible:outline-2 focus-visible:outline-cyan-400";

    const inputStyle = {
        background: "var(--surface-3)",
        color: "var(--text-primary)",
        border: "1px solid var(--border-medium)",
    };

    // =====================================================
    // SUBMIT CREATE TASK
    // =====================================================

    async function submit(
        event: FormEvent<HTMLFormElement>
    ) {
        event.preventDefault();

        if (submitting.current) return;

        const form = event.currentTarget;
        const values = new FormData(form);

        const warehouseId =
            Number(values.get("warehouseId"));

        const pickupStationId =
            Number(values.get("pickupStationId"));

        const deliveryStationId =
            Number(values.get("deliveryStationId"));

        if (
            ![
                warehouseId,
                pickupStationId,
                deliveryStationId,
            ].every(
                value =>
                    Number.isInteger(value) &&
                    value > 0 &&
                    value <= 2147483647
            )
        ) {
            setError(
                "Warehouse and station IDs must be positive integers."
            );
            return;
        }

        const pickupQr = Number(values.get("pickupArUcoId"));
        const deliveryQr = Number(values.get("deliveryArUcoId"));
        if (qrPilotEnabled) {
            if (dispatchMode !== "MANUAL") {
                setError("For the one-robot Webots route pilot, select MANUAL dispatch. AUTO assigns too early for the separate QR setup.");
                return;
            }
            if (!qrPoints.some(p => p.id === pickupQr) ||
                !qrPoints.some(p => p.id === deliveryQr) || pickupQr === deliveryQr) {
                setError("Select two different pickup/drop QR markers from the saved Backend map.");
                return;
            }
        }

        if (pickupStationId === deliveryStationId) {
            setError(
                "Pickup and delivery stations must be different."
            );
            return;
        }

        const weight =
            Number(values.get("payloadWeightKg"));

        if (!Number.isFinite(weight) || weight <= 0) {
            setError(
                "Payload weight must be greater than zero."
            );
            return;
        }

        submitting.current = true;
        setBusy(true);
        onBusyChange?.(true);
        setError("");

        let created: Task | undefined;

        try {
            const scheduledTime = String(
                values.get("scheduledTime") || ""
            );

            created = await addTask({
                warehouseId,
                pickupStationId,
                deliveryStationId,

                priorityLevel: Number(
                    values.get("priorityLevel")
                ),

                packageCode:
                    String(values.get("packageCode") || "")
                        .trim() || null,

                itemDescription:
                    String(values.get("itemDescription") || "")
                        .trim() || null,

                payloadWeightKg: weight,

                scheduledTime:
                    scheduledTime
                        ? new Date(scheduledTime).toISOString()
                        : null,

                // NEW: dispatch mode selected by operator
                dispatchMode,
            });
            if (qrPilotEnabled) {
                const taskId = created.taskId;
                if (!taskId) throw new Error("Task created, but Backend did not return a TaskId for QR configuration.");
                try {
                    await apiClient.post(`/api/v1/demo-routing/tasks/${taskId}/qr`, {
                        pickupArUcoId: pickupQr,
                        deliveryArUcoId: deliveryQr,
                    });
                } catch (routeError) {
                    const message = (routeError as { message?: string }).message ?? "Unknown QR mapping error";
                    created = undefined;
                    throw new Error(`Task #${taskId} was created in PostgreSQL, but QR route setup failed: ${message}. Do NOT assign it until QR configuration is fixed.`);
                }
            }
        }
        catch (err) {
            const apiError = err as {
                message?: string;
                errors?: Record<string, string[]>;
            };

            setError(
                [
                    apiError.message || "Unable to create task.",
                    ...Object.values(apiError.errors || {}).flat(),
                ].join(" ")
            );
        }
        finally {
            submitting.current = false;
            setBusy(false);
            onBusyChange?.(false);
        }

        if (created) {
            form.reset();
            setDispatchMode("MANUAL");
            onCreated(created);
        }
    }

    // =====================================================
    // RENDER FORM
    // =====================================================

    return (
        <form
            onSubmit={submit}
            className="p-6 space-y-5"
            aria-busy={busy}
        >
            <div
                id={`${id}-help`}
                className={
                    "rounded-xl px-4 py-3 " +
                    "text-[12px] leading-relaxed " +
                    "bg-cyan-400/10 text-cyan-600 " +
                    "dark:text-cyan-300"
                }
            >
                Use existing warehouse and station IDs.
                Task tracking codes are generated automatically.
                Required fields are marked *.
            </div>

            <fieldset
                disabled={busy}
                className="space-y-5 disabled:opacity-60"
            >
                {/* ===================================== */}
                {/* 01. TRANSPORT ROUTE */}
                {/* ===================================== */}

                <fieldset
                    className="rounded-xl p-4 space-y-4"
                    style={{
                        border: "1px solid var(--border-subtle)",
                    }}
                >
                    <legend
                        className="px-2 text-[13px] font-bold"
                    >
                        01 / Transport Route
                    </legend>

                    <div>
                        <label
                            htmlFor={`${id}-warehouseId`}
                            className="block text-[12px] mb-1.5"
                        >
                            Warehouse ID *
                        </label>

                        <input
                            id={`${id}-warehouseId`}
                            name="warehouseId"
                            type="number"
                            min={1}
                            max={2147483647}
                            step={1}
                            required
                            autoFocus
                            placeholder="Enter warehouse ID"
                            className={inputClass}
                            style={inputStyle}
                        />
                    </div>

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        {[
                            [
                                "pickupStationId",
                                "Pickup Station ID",
                                "Where the robot collects the package",
                            ],
                            [
                                "deliveryStationId",
                                "Delivery Station ID",
                                "Where the robot delivers the package",
                            ],
                        ].map(([name, label, hint]) => (
                            <div key={name}>
                                <label
                                    htmlFor={`${id}-${name}`}
                                    className="block text-[12px] mb-1.5"
                                >
                                    {label} *
                                </label>

                                <input
                                    id={`${id}-${name}`}
                                    name={name}
                                    type="number"
                                    min={1}
                                    max={2147483647}
                                    step={1}
                                    required
                                    placeholder="Enter station ID"
                                    className={inputClass}
                                    style={inputStyle}
                                />

                                <p
                                    className="mt-1.5 text-[11px]"
                                    style={{
                                        color: "var(--text-faint)",
                                    }}
                                >
                                    {hint}
                                </p>
                            </div>
                        ))}
                    </div>
                    {qrPilotEnabled && (
                        <div className="rounded-xl border p-3 space-y-2" style={{ borderColor: "#0d9488" }}>
                            <p className="text-[12px] font-semibold">Webots QR Route (one-robot test)</p>
                            <p className="text-[11px] opacity-70">These QR markers are separate from the PostgreSQL Station IDs above. Start Webots first. Use MANUAL dispatch.</p>
                            {qrPoints.length === 0 && <p className="text-xs text-red-400">No QR markers loaded. Check Backend Warehouse Maps.</p>}
                            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                                <label className="text-xs">Pickup ArUco QR
                                    <select name="pickupArUcoId" className={inputClass + " mt-1"} style={inputStyle} required>
                                        <option value="">Select pickup QR</option>
                                        {qrPoints.map(p => <option value={p.id} key={p.id}>{p.label} (ID {p.id})</option>)}
                                    </select>
                                </label>
                                <label className="text-xs">Delivery ArUco QR
                                    <select name="deliveryArUcoId" className={inputClass + " mt-1"} style={inputStyle} required>
                                        <option value="">Select delivery QR</option>
                                        {qrPoints.map(p => <option value={p.id} key={p.id}>{p.label} (ID {p.id})</option>)}
                                    </select>
                                </label>
                            </div>
                        </div>
                    )}
                </fieldset>

                {/* ===================================== */}
                {/* 02. PACKAGE DETAILS */}
                {/* ===================================== */}

                <fieldset
                    className="rounded-xl p-4 space-y-4"
                    style={{
                        border: "1px solid var(--border-subtle)",
                    }}
                >
                    <legend
                        className="px-2 text-[13px] font-bold"
                    >
                        02 / Package Details
                    </legend>

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        <div>
                            <label
                                htmlFor={`${id}-package`}
                                className="block text-[12px] mb-1.5"
                            >
                                Package Code(optional)
                            </label>

                            <input
                                id={`${id}-package`}
                                name="packageCode"
                                maxLength={80}
                                placeholder="e.g. PKG-1001"
                                className={inputClass}
                                style={inputStyle}
                            />
                        </div>

                        <div>
                            <label
                                htmlFor={`${id}-weight`}
                                className="block text-[12px] mb-1.5"
                            >
                                Weight(kg) *
                            </label>

                            <input
                                id={`${id}-weight`}
                                name="payloadWeightKg"
                                type="number"
                                min={0.01}
                                max={999999.99}
                                step={0.01}
                                required
                                placeholder="e.g. 0.10"
                                className={inputClass}
                                style={inputStyle}
                            />
                        </div>
                    </div>

                    <div>
                        <label
                            htmlFor={`${id}-description`}
                            className="block text-[12px] mb-1.5"
                        >
                            Item Description(optional)
                        </label>

                        <textarea
                            id={`${id}-description`}
                            name="itemDescription"
                            maxLength={500}
                            rows={2}
                            placeholder={
                                "Describe the package or handling instructions..."
                            }
                            className={`${inputClass} resize-y`}
                            style={inputStyle}
                        />
                    </div>
                </fieldset>

                {/* ===================================== */}
                {/* 03. PRIORITY AND SCHEDULE */}
                {/* ===================================== */}

                <fieldset
                    className="rounded-xl p-4 space-y-4"
                    style={{
                        border: "1px solid var(--border-subtle)",
                    }}
                >
                    <legend
                        className="px-2 text-[13px] font-bold"
                    >
                        03 / Priority & amp; Schedule
                    </legend>

                    <fieldset>
                        <legend
                            className="text-[12px] mb-2"
                        >
                            Priority Level *
                        </legend>

                        <div className="grid grid-cols-1 sm:grid-cols-3 gap-2">
                            {[
                                ["1", "HIGH", "Urgent"],
                                ["2", "MEDIUM", "Normal"],
                                ["3", "LOW", "Flexible"],
                            ].map(([value, label, hint]) => (
                                <label
                                    key={value}
                                    className={
                                        "relative flex items-center gap-3 " +
                                        "rounded-xl border border-slate-500/30 " +
                                        "p-3 text-[12px] cursor-pointer transition-all hover:brightness-110 " +
                                        "has-[:checked]:border-cyan-400 " +
                                        "has-[:checked]:bg-cyan-400/10"
                                    }
                                >
                                    <input
                                        type="radio"
                                        name="priorityLevel"
                                        value={value}
                                        defaultChecked={value === "2"}
                                        required
                                        className="accent-cyan-400 shrink-0"
                                    />

                                    <span>
                                        <span className="block font-mono font-bold">
                                            {label}
                                        </span>

                                        <span
                                            className="block mt-1 text-[11px]"
                                            style={{
                                                color: "var(--text-faint)",
                                            }}
                                        >
                                            {hint}
                                        </span>
                                    </span>
                                </label>
                            ))}
                        </div>
                    </fieldset>

                    <div>
                        <label
                            htmlFor={`${id}-schedule`}
                            className="block text-[12px] mb-1.5"
                        >
                            Scheduled Time(optional)
                        </label>

                        <input
                            id={`${id}-schedule`}
                            name="scheduledTime"
                            type="datetime-local"
                            className={inputClass}
                            style={inputStyle}
                        />

                        <p
                            className="mt-1.5 text-[11px]"
                            style={{
                                color: "var(--text-faint)",
                            }}
                        >
                            Uses your local timezone.
                            Leave blank to start without a schedule.
                        </p>
                    </div>
                </fieldset>

                {/* ===================================== */}
                {/* 04. AUTO / MANUAL DISPATCH */}
                {/* ===================================== */}

                <fieldset
                    className="rounded-xl p-4 space-y-4"
                    style={{
                        border: "1px solid var(--border-subtle)",
                    }}
                >
                    <legend
                        className="px-2 text-[13px] font-bold"
                    >
                        04 / Dispatch Mode *
                    </legend>

                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                        {(["MANUAL", "AUTO"] as const).map(
                            mode => (
                                <label
                                    key={mode}
                                    className={
                                        "flex gap-3 p-3 rounded-xl " +
                                        "border border-slate-500/30 " +
                                        "cursor-pointer transition-all hover:brightness-110 " +
                                        "has-[:checked]:border-cyan-400 " +
                                        "has-[:checked]:bg-cyan-400/10"
                                    }
                                >
                                    <input
                                        type="radio"
                                        name="dispatchMode"
                                        value={mode}
                                        checked={
                                            dispatchMode === mode
                                        }
                                        onChange={() =>
                                            setDispatchMode(mode)
                                        }
                                        className={
                                            "accent-cyan-400 shrink-0"
                                        }
                                    />

                                    <span className="text-[12px]">
                                        <span className="block font-bold">
                                            {mode === "AUTO"
                                                ? "Automatic"
                                                : "Manual"}
                                        </span>

                                        <span
                                            className="block mt-1 text-[11px]"
                                            style={{
                                                color: "var(--text-faint)",
                                            }}
                                        >
                                            {mode === "AUTO"
                                                ? "System selects an eligible robot."
                                                : "Operator selects the robot later."}
                                        </span>
                                    </span>
                                </label>
                            )
                        )}
                    </div>

                    <div
                        className="rounded-lg p-3 text-[12px]"
                        style={{
                            background: "var(--surface-3)",
                            color: "var(--text-muted)",
                        }}
                    >
                        {dispatchMode === "AUTO" ? (
                            <p>
                                <strong>Automatic: </strong>{" "}
                                Backend checks robot status,
                                battery, payload and maintenance,
                                then creates an AUTO assignment.
                                A robot may begin moving after
                                you create this task.
                            </p>
                        ) : (
                            <p>
                                <strong>Manual: </strong>{" "}
                                Task remains QUEUED.
                                After creating it, open the
                                task list and use Assign to
                                select a suitable robot.
                            </p>
                        )}
                    </div>
                </fieldset>
            </fieldset>

            {/* ===================================== */}
            {/* FORM ERROR */}
            {/* ===================================== */}

            {error && (
                <p
                    role="alert"
                    className={
                        "rounded-lg p-3 text-[12px] " +
                        "text-red-400 bg-red-500/10"
                    }
                >
                    {error}
                </p>
            )}

            {/* ===================================== */}
            {/* ACTION BUTTONS */}
            {/* ===================================== */}

            <div
                className="sticky bottom-0 flex gap-3 pt-4 pb-1"
                style={{
                    background: "var(--surface-2)",
                    borderTop: "1px solid var(--border-subtle)",
                }}
            >
                <button
                    type="button"
                    disabled={busy}
                    onClick={onCancel}
                    className={
                        "flex-1 rounded-xl py-3 text-[13px] " +
                        "font-semibold disabled:opacity-50 " +
                        "transition-all hover:-translate-y-0.5 hover:shadow-md hover:brightness-110 active:scale-95 active:translate-y-0 duration-200 disabled:hover:translate-y-0 disabled:hover:scale-100 disabled:hover:shadow-none"
                    }
                    style={{
                        background: "var(--surface-3)",
                        border: "1px solid var(--border-medium)",
                    }}
                >
                    Cancel
                </button>

                <button
                    type="submit"
                    disabled={busy}
                    className={
                        "flex-[2] rounded-xl py-3 text-[13px] " +
                        "font-bold bg-cyan-400 text-slate-950 " +
                        "disabled:opacity-50 " +
                        "transition-all hover:-translate-y-0.5 hover:shadow-lg hover:brightness-110 active:scale-95 active:translate-y-0 duration-200 disabled:hover:translate-y-0 disabled:hover:scale-100 disabled:hover:shadow-none"
                    }
                >
                    {busy
                        ? "Creating..."
                        : dispatchMode === "AUTO"
                            ? "Create & Auto Assign"
                            : "Create Task (Manual)"}
                </button>
            </div>
        </form>
    );
}

// =====================================================
// STANDALONE CREATE PAGE
// =====================================================

export function TaskCreationPage() {
    const navigate = useNavigate();

    return (
        <main
            className="h-full overflow-auto p-6"
            style={{
                background: "var(--background)",
                color: "var(--text-primary)",
            }}
        >
            <section
                className="max-w-2xl mx-auto rounded-2xl shadow-sm border border-slate-500/10"
                style={{
                    background: "var(--surface-2)",
                }}
            >
                <h1 className="px-6 pt-6 font-bold text-lg">
                    Create Transport Task
                </h1>

                <TaskCreateForm
                    onCreated={() =>
                        navigate("/dashboard/tasks")
                    }
                    onCancel={() =>
                        navigate("/dashboard/tasks")
                    }
                />
            </section>
        </main>
    );
}

export default TaskCreationPage;