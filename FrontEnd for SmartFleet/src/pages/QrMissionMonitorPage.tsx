import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { useAuth } from "../context/AuthContext";
import { useCatalog } from "../api/modules/catalog";
import MissionVerificationPanel, { type VerificationMission } from "../components/MissionVerificationPanel";

interface Waypoint {
    arucoId: number;
    row: number;
    col: number;
    stage: string;
}
interface Mission extends VerificationMission {
    taskId: number;
    missionId: number;
    robotCode: string;
    startArUcoId: number;
    pickupArUcoId: number;
    deliveryArUcoId: number;
    nextWaypointIndex: number;
    lastReachedArUcoId: number | null;
    pickupReached: boolean;
    deliveryReached: boolean;
    waypoints: Waypoint[];
    statusHistory: { newStatus: string; source: string; changedBy?: number; reason: string; changedAt: string }[];
}

export default function QrMissionMonitorPage() {
    const { user } = useAuth();
    const { catalog } = useCatalog();
    const point = (id: number) => {
        const station = catalog?.stations.find(s => s.arucoId === id);
        return station ? { name: station.name } : undefined;
    };
    const canVerify = user?.roles.includes("Warehouse Operator") ?? false;
    const [missions, setMissions] = useState<Mission[]>([]);
    const [positions, setPositions] = useState<Record<string, number>>({});
    const [localizations, setLocalizations] = useState<Record<string, {
        headingDeg: number; mapRevision: number; source: string;
    }>>({});
    const [error, setError] = useState("");
    const [lastUpdated, setLastUpdated] = useState("");
    const reload = useCallback(async () => {
            try {
                const [missionResponse, positionResponse, localizationResponse] = await Promise.all([
                    apiClient.get<Mission[]>("/api/v1/missions"),
                    apiClient.get<Record<string, number>>("/api/v1/demo-routing/robots/positions"),
                    apiClient.get("/api/v1/demo-routing/robots/localizations").catch(() => ({ data: {} })),
                ]);
                    setMissions(missionResponse.data);
                    setPositions(positionResponse.data);
                    setLocalizations(localizationResponse.data);
                    setLastUpdated(new Date().toLocaleTimeString());
                    setError("");
            } catch (ex) {
                setError((ex as { message?: string }).message ?? "Cannot load persisted missions");
            }
    }, []);
    useEffect(() => {
        void reload();
        const timer = window.setInterval(() => { void reload(); }, 1500);
        return () => { window.clearInterval(timer); };
    }, [reload]);
    return (
        <main className="sf-data-page flex-1 overflow-auto p-6" style={{ color: "var(--text-primary)" }}>
            <h1 className="text-xl font-bold">Mission progress &amp; delivery verification</h1>
            <Link className="sf-button sf-button-primary mt-3" to="/simulation">Open live tracking &amp; handover →</Link>
            <p className="text-xs mt-2" style={{ color: "var(--text-faint)" }}>
                Live route chosen from the Backend QR map, robot waypoint confirmations, and pickup/drop-off progress.
                Starting QR and heading are localized from camera ArUco pose and IMU.
            </p>
            <p className="text-xs mt-2" style={{ color: "var(--text-faint)" }}>
                Last updated: {lastUpdated || "Connecting"} · Auto-refresh every 1.5 seconds
            </p>
            {error && <p role="alert" className="mt-3 text-red-400">{error}</p>}
            <section className="mt-5 rounded-xl border p-4" style={{ borderColor: "var(--border-medium)", background: "var(--surface-1)" }}>
                <h2 className="font-semibold">Robot checkpoint positions reported to Backend</h2>
                <div className="flex flex-wrap gap-2 mt-3">
                    {Object.keys(positions).length === 0
                        ? <span className="text-sm opacity-60">Waiting for a valid camera/IMU localization on the saved map. Check marker calibration and the robot's camera view.</span>
                        : Object.entries(positions).map(([name, id]) =>
                            <span key={name} className="rounded-md border px-3 py-2 text-sm" style={{ borderColor: "var(--border-medium)" }}>
                                {name} : QR{id}
                                {localizations[name] && <span className="block text-xs opacity-70">
                                    Heading {localizations[name].headingDeg.toFixed(1)}° · {localizations[name].source} · map {localizations[name].mapRevision}
                                </span>}
                            </span>)}
                </div>
            </section>
            {missions.length === 0 &&
                <section className="mt-4 rounded-xl border p-5" style={{ borderColor: "var(--border-medium)" }}>
                    No persisted route yet. Select the pickup and delivery stations when creating a task, then dispatch an eligible robot.
                </section>}
            <div className="grid grid-cols-1 gap-4 mt-4">
                {missions.map(m => (
                    <section key={m.missionId} className="rounded-xl border p-4" style={{ borderColor: "var(--border-medium)", background: "var(--surface-1)" }}>
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <h2 className="text-base font-semibold">Mission M{m.missionId} · Task #{m.taskId} · {m.robotCode}</h2>
                            <strong style={{ color: m.deliveryReached ? "#16a34a" : "#0d9488" }}>
                                {m.missionStatus}
                            </strong>
                        </div>
                        <div className="mt-3 text-sm">Start: QR{m.startArUcoId} · Pickup: QR{m.pickupArUcoId} · Delivery: QR{m.deliveryArUcoId}</div>
                        <p className="mt-1 text-sm">Last confirmed: {m.lastReachedArUcoId === null ? "none" : `QR${m.lastReachedArUcoId}`}</p>
                        <div className="flex flex-wrap gap-2 mt-4">
                            {m.waypoints.map((waypoint, index) => {
                                const reached = index < m.nextWaypointIndex;
                                const active = index === m.nextWaypointIndex;
                                return <div key={index} className="rounded-lg border px-3 py-2 text-center text-xs" style={{
                                    borderColor: reached ? "#0d9488" : active ? "#eab308" : "var(--border-medium)",
                                    background: reached ? "rgba(13,148,136,.13)" : active ? "rgba(234,179,8,.13)" : "var(--surface-3)"
                                }}>
                                    <div className="font-semibold">QR{waypoint.arucoId}</div>
                                    <div className="mt-1 opacity-65">{waypoint.stage}</div>
                                    <div>{reached ? "✓ Reached" : active ? "Next" : "Pending"}</div>
                                </div>;
                            })}
                        </div>
                        <p className="text-xs opacity-60 mt-3">
                            Green means the Backend acknowledged a camera-confirmed waypoint in Webots.
                        </p>
                        <MissionVerificationPanel mission={m} canVerify={canVerify} onSaved={reload} pickupPoint={point(m.pickupArUcoId)} deliveryPoint={point(m.deliveryArUcoId)} />
                        {m.statusHistory?.length > 0 && <details className="text-xs mt-3"><summary>Mission status history</summary>
                            {m.statusHistory.map((h, index) => <p key={index} className="mt-1">{new Date(h.changedAt).toLocaleString()} · {h.newStatus} · {h.source}{h.changedBy ? ` #${h.changedBy}` : ""} · {h.reason}</p>)}
                        </details>}
                    </section>
                ))}
            </div>
        </main>
    );
}
