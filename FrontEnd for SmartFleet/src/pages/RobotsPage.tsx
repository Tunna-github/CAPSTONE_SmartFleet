import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../context/AuthContext";
import {
    getRobots,
    changeRobotStatus,
    resolveRobotIncident,
    type RobotInfo,
} from "../api/modules/robots";

type EditableStatus = "ERROR" | "MAINTENANCE" | "AVAILABLE";

function getDefaultStatus(current: string): EditableStatus {
    if (current === "ERROR") return "MAINTENANCE";
    if (current === "MAINTENANCE") return "AVAILABLE";
    return "ERROR";
}

function getStatusColor(status: string): string {
    switch (status) {
        case "AVAILABLE": return "#22c55e";
        case "ERROR": return "#ef4444";
        case "MAINTENANCE": return "#f59e0b";
        case "BUSY": return "#22d3ee";
        case "CHARGING": return "#a78bfa";
        default: return "#94a3b8";
    }
}

function getErrorMessage(error: unknown): string {
    if (typeof error === "object" && error !== null && "message" in error) {
        const message = error.message;
        if (typeof message === "string" && message.trim()) return message;
    }
    return "Operation failed. Please try again.";
}

export function RobotsPage() {
    const { role } = useAuth();
    const canManage = role === "maintenance" || role === "admin";

    const [robots, setRobots] = useState<RobotInfo[]>([]);
    const [loading, setLoading] = useState(true);
    const [pendingAction, setPendingAction] = useState<string | null>(null);
    const [error, setError] = useState("");
    const [message, setMessage] = useState("");
    const [targetByRobot, setTargetByRobot] = useState<Record<number, EditableStatus>>({});
    const [reasonByRobot, setReasonByRobot] = useState<Record<number, string>>({});
    const [noteByIncident, setNoteByIncident] = useState<Record<number, string>>({});

    const loadRobots = useCallback(async () => {
        const data = await getRobots();
        setRobots(data);
    }, []);

    useEffect(() => {
        let mounted = true;
        getRobots()
            .then((data) => {
                if (mounted) setRobots(data);
            })
            .catch((err: unknown) => {
                if (mounted) setError(getErrorMessage(err));
            })
            .finally(() => {
                if (mounted) setLoading(false);
            });
        return () => {
            mounted = false;
        };
    }, []);

    async function refreshRobots() {
        setLoading(true);
        setError("");
        setMessage("");
        try {
            await loadRobots();
        } catch (err) {
            setError(getErrorMessage(err));
        } finally {
            setLoading(false);
        }
    }

    async function submitStatus(robot: RobotInfo) {
        if (!canManage || pendingAction) return;

        const robotId = robot.robotId;
        const target = targetByRobot[robotId] ?? getDefaultStatus(robot.operationalStatus);
        const reason = (reasonByRobot[robotId] ?? "").trim();

        if (reason.length < 5 || reason.length > 500) {
            setError("Please provide a reason between 5 and 500 characters.");
            return;
        }

        if (target === "AVAILABLE") {
            const confirmed = window.confirm(
                `Confirm ${robot.robotCode} is physically safe, has stopped all missions, ` +
                "and is ready to receive new tasks?"
            );
            if (!confirmed) return;
        }

        setPendingAction(`status-${robotId}`);
        setError("");
        setMessage("");
        try {
            await changeRobotStatus(robotId, target, reason);
            await loadRobots();
            setReasonByRobot((prev) => ({ ...prev, [robotId]: "" }));
            setMessage(`${robot.robotCode} changed to ${target}.`);
        } catch (err) {
            setError(getErrorMessage(err));
        } finally {
            setPendingAction(null);
        }
    }

    async function submitResolution(robot: RobotInfo, incidentId: number) {
        if (!canManage || pendingAction) return;

        const note = (noteByIncident[incidentId] ?? "").trim();
        if (note.length < 5 || note.length > 1000) {
            setError("Resolution note must be between 5 and 1000 characters.");
            return;
        }

        setPendingAction(`incident-${incidentId}`);
        setError("");
        setMessage("");
        try {
            await resolveRobotIncident(robot.robotId, incidentId, note);
            await loadRobots();
            setNoteByIncident((prev) => ({ ...prev, [incidentId]: "" }));
            setMessage(`Incident #${incidentId} was resolved.`);
        } catch (err) {
            setError(getErrorMessage(err));
        } finally {
            setPendingAction(null);
        }
    }

    return (
        <main
            className="flex flex-col h-full overflow-hidden transition-colors"
            style={{ background: "var(--background)", color: "var(--text-primary)" }}
        >
            {/* Header Area */}
            <div className="shrink-0 px-6 py-5" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                <div className="flex items-center justify-between gap-4">
                    <div>
                        <h1 className="text-[20px] font-bold tracking-tight">Robot Fleet &amp; Maintenance</h1>
                        <p className="mt-1 text-[12px] font-mono" style={{ color: "var(--text-faint)" }}>
                            Live status from SmartFleet Backend / Aiven PostgreSQL
                        </p>
                    </div>
                    <button
                        type="button"
                        onClick={() => void refreshRobots()}
                        disabled={loading || pendingAction !== null}
                        className="flex items-center gap-2 rounded-xl px-4 py-2 text-[13px] font-bold transition-all hover:scale-[1.02] hover:brightness-110 active:scale-95 duration-200 disabled:opacity-50 disabled:cursor-not-allowed disabled:hover:scale-100"
                        style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)" }}
                    >
                        {loading ? (
                            <span className="flex items-center gap-2">
                                <span className="w-3 h-3 border-2 border-cyan-400 border-t-transparent rounded-full animate-spin" />
                                Refreshing...
                            </span>
                        ) : "Refresh Fleet"}
                    </button>
                </div>
            </div>

            {/* Scrollable Content */}
            <div className="flex-1 overflow-y-auto p-6 space-y-6">
                
                {/* Alerts */}
                {error && (
                    <div role="alert" className="flex items-center gap-3 rounded-xl bg-red-500/10 border border-red-500/30 p-4 text-[13px] font-medium text-red-400 shadow-sm animate-in fade-in slide-in-from-top-2">
                        <span className="w-6 h-6 rounded-full bg-red-500/20 flex items-center justify-center shrink-0">!</span>
                        {error}
                    </div>
                )}
                {message && (
                    <div role="status" className="flex items-center gap-3 rounded-xl bg-green-500/10 border border-green-500/30 p-4 text-[13px] font-medium text-green-400 shadow-sm animate-in fade-in slide-in-from-top-2">
                        <span className="w-6 h-6 rounded-full bg-green-500/20 flex items-center justify-center shrink-0">✓</span>
                        {message}
                    </div>
                )}

                {!loading && robots.length === 0 && (
                    <div className="text-center py-12 rounded-2xl border border-dashed" style={{ borderColor: "var(--border-medium)", background: "var(--surface-2)" }}>
                        <p className="text-[14px]" style={{ color: "var(--text-muted)" }}>No robots found in the database.</p>
                    </div>
                )}

                {/* Grid */}
                <div className="grid grid-cols-1 xl:grid-cols-2 gap-5">
                    {robots.map((robot) => {
                        const robotId = robot.robotId;
                        const target = targetByRobot[robotId] ?? getDefaultStatus(robot.operationalStatus);
                        const blocked =
                            !robot.isActive ||
                            robot.hasActiveAssignment ||
                            robot.operationalStatus === "BUSY" ||
                            robot.operationalStatus === "CHARGING";
                        const disabled = blocked || pendingAction !== null;
                        const color = getStatusColor(robot.operationalStatus);

                        return (
                            <section
                                key={robotId}
                                className="flex flex-col rounded-2xl overflow-hidden transition-all duration-300 hover:-translate-y-1 hover:shadow-xl group"
                                style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                            >
                                {/* Card Header */}
                                <div className="px-5 py-4 flex items-start justify-between gap-3" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                                    <div>
                                        <div className="flex items-center gap-2">
                                            <h2 className="text-[16px] font-bold">{robot.robotCode}</h2>
                                            {!robot.isActive && (
                                                <span className="px-2 py-0.5 rounded text-[9px] font-bold tracking-wider bg-gray-500/20 text-gray-400">INACTIVE</span>
                                            )}
                                        </div>
                                        <p className="mt-1 text-[11px] font-mono uppercase tracking-wide" style={{ color: "var(--text-faint)" }}>
                                            {robot.model}
                                        </p>
                                    </div>
                                    <div
                                        className="rounded-lg px-3 py-1 text-[11px] font-bold tracking-wide flex items-center gap-1.5 shadow-sm"
                                        style={{ background: `${color}15`, color, border: `1px solid ${color}40` }}
                                    >
                                        <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: color }} />
                                        {robot.operationalStatus}
                                    </div>
                                </div>

                                {/* Stats Strip */}
                                <div className="grid grid-cols-4 divide-x divide-[var(--border-subtle)]" style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                                    {[
                                        { label: "Connection", val: robot.connectionStatus, highlight: robot.connectionStatus === "ONLINE" },
                                        { label: "Battery", val: robot.batteryPercent === null ? "N/A" : `${robot.batteryPercent}%`, highlight: (robot.batteryPercent ?? 0) > 20 },
                                        { label: "Mission", val: robot.hasActiveAssignment ? "ACTIVE" : "IDLE", highlight: robot.hasActiveAssignment },
                                        { label: "Maintenance", val: robot.hasActiveMaintenance ? "YES" : "NO", highlight: robot.hasActiveMaintenance }
                                    ].map((stat, idx) => (
                                        <div key={idx} className="px-4 py-3 text-center transition-colors group-hover:bg-white/5">
                                            <p className="text-[10px] font-mono font-semibold tracking-wider mb-1" style={{ color: "var(--text-faint)" }}>{stat.label}</p>
                                            <p className="text-[12px] font-bold" style={{ color: stat.highlight ? "var(--text-primary)" : "var(--text-muted)" }}>
                                                {stat.val}
                                            </p>
                                        </div>
                                    ))}
                                </div>

                                <div className="p-5 space-y-6 flex-1 flex flex-col">
                                    {/* Action Controls */}
                                    {canManage && (
                                        <div className="space-y-3">
                                            <h3 className="text-[11px] font-mono font-bold tracking-wider" style={{ color: "var(--text-faint)" }}>OPERATIONAL CONTROLS</h3>
                                            <div className="flex gap-3">
                                                <select
                                                    aria-label={`Target status for ${robot.robotCode}`}
                                                    value={target}
                                                    disabled={disabled}
                                                    onChange={(event) => {
                                                        const selected = event.currentTarget.value as EditableStatus;
                                                        if (["ERROR", "MAINTENANCE", "AVAILABLE"].includes(selected)) {
                                                            setTargetByRobot((prev) => ({ ...prev, [robotId]: selected }));
                                                        }
                                                    }}
                                                    className="flex-1 rounded-lg px-3 py-2.5 text-[13px] font-medium outline-none focus-visible:outline-2 focus-visible:outline-cyan-400 transition-colors"
                                                    style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
                                                >
                                                    <option value="ERROR">Report Fault (ERROR)</option>
                                                    <option value="MAINTENANCE">Start Repair (MAINTENANCE)</option>
                                                    <option value="AVAILABLE">Return to Service (AVAILABLE)</option>
                                                </select>
                                                <button
                                                    type="button"
                                                    onClick={() => void submitStatus(robot)}
                                                    disabled={disabled}
                                                    className="px-5 rounded-lg text-[13px] font-bold transition-all hover:-translate-y-0.5 hover:shadow-md hover:brightness-110 active:scale-95 active:translate-y-0 duration-200 disabled:opacity-40 disabled:hover:translate-y-0 disabled:hover:scale-100 disabled:cursor-not-allowed"
                                                    style={{ background: "linear-gradient(135deg, #22d3ee, #0ea5e9)", color: "#0f172a" }}
                                                >
                                                    {pendingAction === `status-${robotId}` ? "Updating..." : `Set ${target}`}
                                                </button>
                                            </div>
                                            <textarea
                                                aria-label={`Reason for ${robot.robotCode}`}
                                                value={reasonByRobot[robotId] ?? ""}
                                                onChange={(e) => setReasonByRobot((prev) => ({ ...prev, [robotId]: e.currentTarget.value }))}
                                                disabled={disabled}
                                                maxLength={500}
                                                rows={2}
                                                placeholder="Provide a required reason for this status change..."
                                                className="w-full rounded-lg p-3 text-[13px] outline-none focus-visible:outline-2 focus-visible:outline-cyan-400 transition-colors resize-none"
                                                style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
                                            />
                                            {blocked && (
                                                <div className="flex items-start gap-2 rounded-lg bg-amber-500/10 border border-amber-500/20 p-2.5 text-amber-400/90 text-[11px] leading-tight">
                                                    <span className="mt-0.5">ⓘ</span>
                                                    <span>Robot is busy, charging, inactive, or assigned. Resolve its current state before overriding status.</span>
                                                </div>
                                            )}
                                        </div>
                                    )}

                                    {/* Incidents Section */}
                                    <div className="mt-auto space-y-3 pt-5" style={{ borderTop: "1px dashed var(--border-subtle)" }}>
                                        <div className="flex items-center justify-between">
                                            <h3 className="text-[11px] font-mono font-bold tracking-wider flex items-center gap-2" style={{ color: "var(--text-faint)" }}>
                                                OPEN INCIDENTS 
                                                <span className="bg-red-500/20 text-red-400 px-1.5 py-0.5 rounded text-[9px]">{robot.openIncidents.length}</span>
                                            </h3>
                                        </div>

                                        {robot.openIncidents.length === 0 ? (
                                            <p className="text-[12px] italic opacity-60">No unresolved incidents.</p>
                                        ) : (
                                            <div className="space-y-3">
                                                {robot.openIncidents.map((incident) => (
                                                    <div
                                                        key={incident.incidentId}
                                                        className="space-y-3 rounded-xl p-4 border transition-colors hover:bg-white/[0.02]"
                                                        style={{ background: "var(--surface-3)", borderColor: "rgba(239, 68, 68, 0.2)" }}
                                                    >
                                                        <div>
                                                            <div className="flex items-center justify-between mb-1">
                                                                <span className="text-[11px] font-mono font-bold text-red-400">
                                                                    #{incident.incidentId} • {incident.severity}
                                                                </span>
                                                                <span className="text-[10px] font-bold px-2 py-0.5 rounded bg-red-500/10 text-red-400">{incident.incidentType}</span>
                                                            </div>
                                                            <p className="text-[13px] mt-1 text-[var(--text-primary)] leading-relaxed">
                                                                {incident.description || "No description provided"}
                                                            </p>
                                                        </div>

                                                        {canManage && (
                                                            <div className="flex gap-2 pt-2" style={{ borderTop: "1px solid rgba(239,68,68,0.1)" }}>
                                                                <input
                                                                    type="text"
                                                                    aria-label={`Resolve incident ${incident.incidentId}`}
                                                                    value={noteByIncident[incident.incidentId] ?? ""}
                                                                    onChange={(e) => setNoteByIncident((prev) => ({ ...prev, [incident.incidentId]: e.currentTarget.value }))}
                                                                    maxLength={1000}
                                                                    disabled={pendingAction !== null}
                                                                    placeholder="Resolution note..."
                                                                    className="flex-1 rounded-lg px-3 py-2 text-[12px] outline-none focus-visible:outline-2 focus-visible:outline-green-400 transition-colors"
                                                                    style={{ background: "var(--surface-2)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
                                                                />
                                                                <button
                                                                    type="button"
                                                                    onClick={() => void submitResolution(robot, incident.incidentId)}
                                                                    disabled={pendingAction !== null || !(noteByIncident[incident.incidentId]?.length >= 5)}
                                                                    className="shrink-0 rounded-lg px-4 py-2 text-[12px] font-bold transition-all hover:-translate-y-0.5 hover:shadow-md hover:brightness-110 active:scale-95 active:translate-y-0 duration-200 disabled:opacity-40 disabled:hover:translate-y-0 disabled:hover:scale-100 disabled:cursor-not-allowed"
                                                                    style={{ background: "rgba(34,197,94,0.15)", color: "#4ade80", border: "1px solid rgba(34,197,94,0.3)" }}
                                                                >
                                                                    {pendingAction === `incident-${incident.incidentId}` ? "..." : "Resolve"}
                                                                </button>
                                                            </div>
                                                        )}
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </div>
                                </div>
                            </section>
                        );
                    })}
                </div>
            </div>
        </main>
    );
}

export default RobotsPage;