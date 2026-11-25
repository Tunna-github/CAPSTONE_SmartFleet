import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import { useWarehouseMap } from "../context/WarehouseMapContext";
import type { RobotInfo } from "../api/modules/robots";
import { WarehouseGrid } from "./WarehouseGrid";
import useRobotPoses from "../hooks/useRobotPoses";

interface Mission {
    robotCode: string; missionId: number; taskId: number; mapRevision: number;
    lastReachedArUcoId: number | null; missionStatus: string; assignmentStatus: string;
    waypoints: { arucoId: number; row: number; col: number }[];
}
const colors = ["#22d3ee", "#a78bfa", "#fb923c", "#34d399", "#f472b6", "#facc15"];

export default function LiveFleetMap({ robots }: { robots: RobotInfo[] }) {
    const { warehouseLayout, links, mapError, mapRevision, reloadMap } = useWarehouseMap();
    const { poses, connected, serverNow } = useRobotPoses(mapRevision);
    const [positions, setPositions] = useState<Record<string, number>>({});
    const [missions, setMissions] = useState<Mission[]>([]);
    const [selected, setSelected] = useState("");
    const [error, setError] = useState("");
    useEffect(() => {
        const timer=window.setInterval(()=>void reloadMap().catch(()=>{}),15000);
        return ()=>window.clearInterval(timer);
    },[reloadMap]);
    useEffect(() => {
        let disposed = false;
        async function refresh() {
            try {
                const [localized, routed] = await Promise.all([
                    apiClient.get<Record<string, number>>("/api/v1/demo-routing/robots/positions"),
                    apiClient.get<Mission[]>("/api/v1/missions"),
                ]);
                if (!disposed) { setPositions(localized.data); setMissions(Array.isArray(routed.data) ? routed.data : []); setError(""); }
            } catch { if (!disposed) setError("Checkpoint/mission snapshot unavailable; live pose freshness is shown separately."); }
        }
        void refresh(); const timer = window.setInterval(() => void refresh(), 5000);
        return () => { disposed = true; window.clearInterval(timer); };
    }, [mapRevision]);
    const points = warehouseLayout.flatMap((row, y) => row.flatMap((cell, x) =>
        cell.type === "qr" && cell.arucoId != null ? [{ id: cell.arucoId, x, y }] : []));
    const active = missions.filter(m => m.mapRevision === mapRevision && m.assignmentStatus === "ACTIVE");
    const markers = robots.flatMap((robot, index) => {
        const mission = active.find(m => m.robotCode === robot.robotCode);
        const pose = poses[robot.robotCode];
        const hasPose = pose?.mapRevision === mapRevision;
        const fresh = hasPose && serverNow - Date.parse(pose.capturedAtUtc) < 3000;
        const point = points.find(p => p.id === (mission?.lastReachedArUcoId ?? positions[robot.robotCode]));
        if (!hasPose && !point) return [];
        const color = fresh ? colors[index % colors.length] : hasPose ? "#94a3b8" : "#eab308";
        return [{ id: robot.robotCode, x: hasPose ? pose.x : point!.x, y: hasPose ? pose.y : point!.y,
            tx: hasPose ? pose.x : point!.x, ty: hasPose ? pose.y : point!.y,
            heading: hasPose ? pose.yaw : undefined, battery: robot.batteryPercent ?? 0,
            mqtt: (robot.connectionStatus === "ONLINE" && (!hasPose || fresh) ? "ONLINE" : "OFFLINE") as "ONLINE" | "OFFLINE",
            missionId: null, color, positionLabel: hasPose ? `${fresh ? "Live" : "Stale"} simulation pose · ${new Date(pose.capturedAtUtc).toLocaleTimeString()}` : "Last camera-confirmed QR" }];
    });
    const routes = active.filter(m => !selected || selected === m.robotCode).map(m => ({ robotId: m.robotCode,
        color: colors[Math.max(0, robots.findIndex(r => r.robotCode === m.robotCode)) % colors.length],
        points: (m.waypoints ?? []).map(w => ({ x: w.col, y: w.row })) }));
    const chosen = robots.find(r => r.robotCode === selected);
    const chosenMission = active.find(m => m.robotCode === selected);
    return <div className="h-full flex flex-col min-h-0">
        <div className="flex items-center flex-wrap gap-3 px-3 py-2 text-xs">
            <span role={mapError || error ? "alert" : "status"}>{mapError || error || `Map ${mapRevision ?? "loading"} · ${markers.length} located robots · ${connected ? "SignalR connected" : "Snapshot/reconnecting"}`}</span>
            <label className="ml-auto">Follow robot <select aria-label="Follow robot" className="ml-2 rounded border bg-[var(--surface-2)] p-1" value={selected} onChange={e => setSelected(e.target.value)}>
                <option value="">All robots</option>{robots.map(r => <option key={r.robotCode} value={r.robotCode}>{r.robotCode}</option>)}
            </select></label>
        </div>
        <p className="px-3 pb-1 text-[11px] opacity-65">Cyan/colored: measured simulation position · Gray: stale pose · Yellow: last QR only. Simulation ground truth is for visualization.</p>
        <div className="flex-1 min-h-0"><WarehouseGrid robots={markers} grid={warehouseLayout} links={links} focusQrNetwork
            missionRoutes={routes} onRobotClick={setSelected} animateRobots={false} /></div>
        <div className="flex flex-wrap gap-2 p-2 border-t border-[var(--border-subtle)]" aria-label="Live robot status">
            {robots.map((robot,index) => {
                const mission=active.find(m=>m.robotCode===robot.robotCode);
                return <button key={robot.robotCode} onClick={()=>setSelected(robot.robotCode)}
                    className="rounded-lg border border-[var(--border-subtle)] px-3 py-2 text-left text-xs flex-1 min-w-[200px]">
                    <strong style={{color:colors[index%colors.length]}}>{robot.robotCode}</strong> · {robot.operationalStatus} · {robot.connectionStatus}
                    <span className="block mt-1">Battery {robot.batteryPercent ?? "?"}% · {mission ? `Task #${mission.taskId} · ${mission.missionStatus}` : "No active task"}</span>
                </button>;
            })}
        </div>
        {chosen && <p className="px-3 py-2 text-xs border-t border-[var(--border-subtle)]">{chosen.robotCode} · {chosen.operationalStatus} · {chosen.connectionStatus} · Battery {chosen.batteryPercent ?? "?"}%
            {chosenMission ? ` · Task #${chosenMission.taskId} · ${chosenMission.missionStatus}` : " · No active mission"}
            {poses[chosen.robotCode] && ` · Heading ${poses[chosen.robotCode].yaw.toFixed(1)}°`}</p>}
    </div>;
}
