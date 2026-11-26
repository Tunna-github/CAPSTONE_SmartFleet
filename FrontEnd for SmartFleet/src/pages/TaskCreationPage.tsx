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
import { useCatalog } from "../api/modules/catalog";

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
    const {catalog,error:catalogError,reload:reloadCatalog}=useCatalog();
    const [warehouseIdSelected,setWarehouseIdSelected]=useState(0);
    const [pickupSelected,setPickupSelected]=useState(0);
    const [deliverySelected,setDeliverySelected]=useState(0);
    const warehouse = warehouseIdSelected || catalog?.warehouses.find(w=>w.isActive && w.warehouseId===catalog.navigationWarehouseId)?.warehouseId || catalog?.warehouses.find(w=>w.isActive)?.warehouseId || 0;
    const availableStations = catalog?.stations.filter(s=>s.warehouseId===warehouse && s.usable) ?? [];
    const pickupStation = availableStations.find(s=>s.stationId===pickupSelected && s.type==="PICKUP");
    const deliveryStation = availableStations.find(s=>s.stationId===deliverySelected && s.type==="DELIVERY");

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

        if (!pickupStation || !deliveryStation) {
            setError("Choose active mapped pickup and delivery stations. Reload the catalog if configuration changed.");
            return;
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
                // Navigation checkpoints are resolved from StationID by the server.
            });
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
            setPickupSelected(0);setDeliverySelected(0);
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
            style={{ color: "var(--text-primary)" }}
            aria-busy={busy}
        >
            <div
                id={`${id}-help`}
                className={
                    "rounded-xl px-4 py-3 " +
                    "text-[12px] leading-relaxed " +
                    "bg-cyan-400/10"
                }
                style={{ color: "var(--text-secondary)" }}
            >
                Choose a warehouse and its commissioned pickup/delivery stations.
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

                    {catalogError && <p role="alert" className="text-red-500">{catalogError}</p>}
                    {catalog && (!availableStations.some(s=>s.type==="PICKUP") || !availableStations.some(s=>s.type==="DELIVERY")) &&
                        <p className="text-sm" role="status">This warehouse needs active mapped pickup and delivery stations. Ask an Admin to configure them in Warehouse &amp; Stations, then reload stations.</p>}
                    <button type="button" className="border rounded px-3 py-1" onClick={()=>void reloadCatalog()}>Reload stations</button>
                    <label className="block text-sm">Warehouse *
                        <select aria-label="Warehouse" name="warehouseId" className={inputClass} style={inputStyle} required value={warehouse} onChange={e=>{setWarehouseIdSelected(Number(e.target.value));setPickupSelected(0);setDeliverySelected(0);}}>
                            <option value="">Select warehouse</option>{catalog?.warehouses.filter(w=>w.isActive).map(w=><option key={w.warehouseId} value={w.warehouseId}>{w.code} — {w.name}</option>)}
                        </select>
                    </label>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        <label className="text-sm">Pickup station *
                            <select aria-label="Pickup station" name="pickupStationId" className={inputClass} style={inputStyle} required value={pickupSelected||""} onChange={e=>setPickupSelected(Number(e.target.value))}>
                                <option value="">Select pickup station</option>{availableStations.filter(s=>s.type==="PICKUP").map(s=><option value={s.stationId} key={s.stationId}>{s.code} — {s.name} · QR{s.arucoId}</option>)}
                            </select>
                        </label>
                        <label className="text-sm">Delivery station *
                            <select aria-label="Delivery station" name="deliveryStationId" className={inputClass} style={inputStyle} required value={deliverySelected||""} onChange={e=>setDeliverySelected(Number(e.target.value))}>
                                <option value="">Select delivery station</option>{availableStations.filter(s=>s.type==="DELIVERY").map(s=><option value={s.stationId} key={s.stationId}>{s.code} — {s.name} · QR{s.arucoId}</option>)}
                            </select>
                        </label>
                    </div>
                    <div className="rounded-xl border p-3 text-sm" style={{borderColor:"#0d9488"}} role="status">
                        Navigation resolved by station: {pickupStation?`${pickupStation.code} → QR${pickupStation.arucoId}`:"Choose pickup"} · {deliveryStation?`${deliveryStation.code} → QR${deliveryStation.arucoId}`:"Choose delivery"}.
                        <p className="text-xs mt-1">The backend validates current station mappings before dispatch. Station verification payloads are separate from navigation QR IDs.</p>
                    </div>
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
                                ["3", "HIGH", "Urgent"],
                                ["2", "MEDIUM", "Normal"],
                                ["1", "LOW", "Flexible"],
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
                                                color: "var(--text-muted)",
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
                                color: "var(--text-muted)",
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
                                                color: "var(--text-muted)",
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
