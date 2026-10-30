
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
            className="h-full overflow-y-auto p-6"
            style={{ background: "var(--background)", color: "var(--text-primary)" }}
        >
            <header className="mb-6 flex items-center justify-between gap-4">
                <div>
                    <h1 className="text-xl font-bold">Robot Fleet &amp; Maintenance</h1>
                    <p className="mt-1 text-xs" style={{ color: "var(--text-faint)" }}>
                        Robot status from SmartFleet Backend / Aiven PostgreSQL
                    </p>
                </div>
                <button
                    type="button"
                    onClick={() => void refreshRobots()}
                    disabled={loading || pendingAction !== null}
                    className="rounded-lg px-4 py-2 text-sm font-semibold disabled:opacity-50"
                    style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)" }}
                >
                    {loading ? "Loading..." : "Refresh"}
                </button>
            </header>

            {error && (
                <p role="alert" className="mb-4 rounded-xl bg-red-500/10 p-3 text-sm text-red-400">
                    {error}
                </p>
            )}
            {message && (
                <p role="status" className="mb-4 rounded-xl bg-green-500/10 p-3 text-sm text-green-400">
                    {message}
                </p>
            )}
            {!loading && robots.length === 0 && (
                <p className="text-sm">No robots found in the database.</p>
            )}

            <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
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
                            className="space-y-4 rounded-2xl p-5"
                            style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                        >
                            <div className="flex items-start justify-between gap-3">
                                <div>
                                    <h2 className="text-base font-bold">{robot.robotCode}</h2>
                                    <p className="mt-1 text-xs" style={{ color: "var(--text-faint)" }}>
                                        {robot.model}
                                    </p>
                                </div>
                                <span
                                    className="rounded-lg px-3 py-1 text-xs font-bold"
                                    style={{ color, border: `1px solid ${color}` }}
                                >
                                    {robot.operationalStatus}
                                </span>
                            </div>

                            <div className="grid grid-cols-2 gap-3 text-sm">
                                <div>
                                    <p className="text-xs opacity-60">Connection</p>
                                    <p className="font-semibold">{robot.connectionStatus}</p>
                                </div>
                                <div>
                                    <p className="text-xs opacity-60">Battery</p>
                                    <p className="font-semibold">
                                        {robot.batteryPercent === null ? "N/A" : `${robot.batteryPercent}%`}
                                    </p>
                                </div>
                                <div>
                                    <p className="text-xs opacity-60">Active Assignment</p>
                                    <p>{robot.hasActiveAssignment ? "YES" : "NO"}</p>
                                </div>
                                <div>
                                    <p className="text-xs opacity-60">Active Maintenance</p>
                                    <p>{robot.hasActiveMaintenance ? "YES" : "NO"}</p>
                                </div>
                            </div>

                            {canManage && (
                                <div
                                    className="space-y-3 pt-4"
                                    style={{ borderTop: "1px solid var(--border-subtle)" }}
                                >
                                    <h3 className="text-sm font-semibold">Change Operational Status</h3>
                                    <select
                                        aria-label={`Target status for ${robot.robotCode}`}
                                        value={target}
                                        disabled={disabled}
                                        onChange={(event) => {
                                            const selected = event.currentTarget.value;
                                            if (
                                                selected === "ERROR" ||
                                                selected === "MAINTENANCE" ||
                                                selected === "AVAILABLE"
                                            ) {
                                                setTargetByRobot((prev) => ({
                                                    ...prev,
                                                    [robotId]: selected,
                                                }));
                                            }
                                        }}
                                        className="w-full rounded-lg p-2 text-sm"
                                        style={{
                                            background: "var(--surface-3)",
                                            border: "1px solid var(--border-medium)",
                                        }}
                                    >
                                        <option value="ERROR">ERROR - Report fault</option>
                                        <option value="MAINTENANCE">MAINTENANCE - Start repair</option>
                                        <option value="AVAILABLE">AVAILABLE - Return to service</option>
                                    </select>

                                    <textarea
                                        aria-label={`Reason for ${robot.robotCode}`}
                                        value={reasonByRobot[robotId] ?? ""}
                                        onChange={(event) => {
                                            const value = event.currentTarget.value;
                                            setReasonByRobot((prev) => ({ ...prev, [robotId]: value }));
                                        }}
                                        disabled={disabled}
                                        maxLength={500}
                                        rows={2}
                                        placeholder="Reason for the status change (required)"
                                        className="w-full rounded-lg p-3 text-sm"
                                        style={{
                                            background: "var(--surface-3)",
                                            border: "1px solid var(--border-medium)",
                                        }}
                                    />

                                    <button
                                        type="button"
                                        onClick={() => void submitStatus(robot)}
                                        disabled={disabled}
                                        className="w-full rounded-lg bg-cyan-400 p-3 text-sm font-bold text-slate-950 disabled:opacity-40"
                                    >
                                        {pendingAction === `status-${robotId}` ? "Updating..." : `Set ${target}`}
                                    </button>

                                    {blocked && (
                                        <p className="text-xs text-amber-400">
                                            This robot is busy, charging, inactive, or has an active assignment.
                                            Resolve its operational state before changing status.
                                        </p>
                                    )}
                                </div>
                            )}

                            <div
                                className="space-y-3 pt-4"
                                style={{ borderTop: "1px solid var(--border-subtle)" }}
                            >
                                <h3 className="text-sm font-semibold">
                                    Open Incidents ({robot.openIncidents.length})
                                </h3>

                                {robot.openIncidents.length === 0 && (
                                    <p className="text-xs opacity-60">No unresolved incidents.</p>
                                )}

                                {robot.openIncidents.map((incident) => (
                                    <div
                                        key={incident.incidentId}
                                        className="space-y-2 rounded-xl p-3"
                                        style={{ background: "var(--surface-3)" }}
                                    >
                                        <p className="text-xs font-semibold text-red-400">
                                            #{incident.incidentId} - {incident.severity} - {incident.incidentType}
                                        </p>
                                        <p className="text-xs">
                                            {incident.description || "No description"}
                                        </p>

                                        {canManage && (
                                            <>
                                                <input
                                                    aria-label={`Resolve incident ${incident.incidentId}`}
                                                    value={noteByIncident[incident.incidentId] ?? ""}
                                                    onChange={(event) => {
                                                        const value = event.currentTarget.value;
                                                        setNoteByIncident((prev) => ({
                                                            ...prev,
                                                            [incident.incidentId]: value,
                                                        }));
                                                    }}
                                                    maxLength={1000}
                                                    disabled={pendingAction !== null}
                                                    placeholder="Resolution note (required)"
                                                    className="w-full rounded-lg p-2 text-sm"
                                                    style={{ background: "var(--surface-2)" }}
                                                />
                                                <button
                                                    type="button"
                                                    onClick={() => void submitResolution(robot, incident.incidentId)}
                                                    disabled={pendingAction !== null}
                                                    className="rounded-lg bg-green-500/10 px-3 py-2 text-xs font-semibold text-green-400 disabled:opacity-40"
                                                >
                                                    {pendingAction === `incident-${incident.incidentId}`
                                                        ? "Resolving..."
                                                        : "Resolve Incident"}
                                                </button>
                                            </>
                                        )}
                                    </div>
                                ))}
                            </div>
                        </section>
                    );
                })}
            </div>
        </main>
    );
}

export default RobotsPage;
