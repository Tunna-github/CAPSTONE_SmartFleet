import { useEffect, useState } from "react";
import { apiClient } from "../api/client";

interface Waypoint {
    arucoId: number;
    row: number;
    col: number;
    stage: string;
}
interface Mission {
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
}

export default function QrMissionMonitorPage() {
    const [missions, setMissions] = useState<Mission[]>([]);
    const [positions, setPositions] = useState<Record<string, number>>({});
    const [localizations, setLocalizations] = useState<Record<string, {
        headingDeg: number; mapRevision: number; source: string;
    }>>({});
    const [error, setError] = useState("");
    const [lastUpdated, setLastUpdated] = useState("");
    useEffect(() => {
        let stopped = false;
        async function reload() {
            try {
                const [missionResponse, positionResponse, localizationResponse] = await Promise.all([
                    apiClient.get<Mission[]>("/api/v1/demo-routing/missions"),
                    apiClient.get<Record<string, number>>("/api/v1/demo-routing/robots/positions"),
                    apiClient.get("/api/v1/demo-routing/robots/localizations").catch(() => ({ data: {} })),
                ]);
                if (!stopped) {
                    setMissions(missionResponse.data);
                    setPositions(positionResponse.data);
                    setLocalizations(localizationResponse.data);
                    setLastUpdated(new Date().toLocaleTimeString());
                    setError("");
                }
            } catch (ex) {
                if (!stopped) setError((ex as { message?: string }).message ?? "Cannot load demo routes");
            }
        }
        void reload();
        const timer = window.setInterval(() => { void reload(); }, 1500);
        return () => { stopped = true; window.clearInterval(timer); };
    }, []);
    return (
        <main className="flex-1 overflow-auto p-6" style={{ color: "var(--text-primary)" }}>
            <h1 className="text-xl font-bold">Webots QR Mission Monitor</h1>
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
                    No routed mission yet. Create a MANUAL task, select Pickup QR and Delivery QR, then assign R01.
                </section>}
            <div className="grid grid-cols-1 gap-4 mt-4">
                {missions.map(m => (
                    <section key={m.missionId} className="rounded-xl border p-4" style={{ borderColor: "var(--border-medium)", background: "var(--surface-1)" }}>
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <h2 className="text-base font-semibold">Mission M{m.missionId} · Task #{m.taskId} · {m.robotCode}</h2>
                            <strong style={{ color: m.deliveryReached ? "#16a34a" : "#0d9488" }}>
                                {m.deliveryReached ? "DELIVERY REACHED" : m.pickupReached ? "GOING TO DELIVERY" : "GOING TO PICKUP"}
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
                    </section>
                ))}
            </div>
        </main>
    );
}
