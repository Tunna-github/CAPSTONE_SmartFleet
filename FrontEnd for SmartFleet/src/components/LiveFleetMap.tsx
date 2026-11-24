import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import { useWarehouseMap } from "../context/WarehouseMapContext";
import type { RobotInfo } from "../api/modules/robots";
import { WarehouseGrid } from "./WarehouseGrid";

export default function LiveFleetMap({ robots }: { robots: RobotInfo[] }) {
    const { warehouseLayout, links, mapError, mapRevision } = useWarehouseMap();
    const [positions, setPositions] = useState<Record<string, number>>({});
    const [error, setError] = useState("");
    useEffect(() => {
        let disposed = false;
        async function refresh() {
            try {
                const [{ data: localized }, { data: missions }] = await Promise.all([
                    apiClient.get<Record<string, number>>("/api/v1/demo-routing/robots/positions"),
                    apiClient.get<Array<{ robotCode: string; missionId: number; mapRevision: number; lastReachedArUcoId: number | null }>>("/api/v1/demo-routing/missions"),
                ]);
                const confirmed = { ...localized };
                for (const robot of robots.filter(r => r.hasActiveAssignment && r.connectionStatus === "ONLINE")) {
                    const mission = missions.filter(m => m.robotCode === robot.robotCode && m.mapRevision === mapRevision)
                        .sort((a, b) => b.missionId - a.missionId)[0];
                    if (mission?.lastReachedArUcoId != null) confirmed[robot.robotCode] = mission.lastReachedArUcoId;
                }
                if (!disposed) { setPositions(confirmed); setError(""); }
            } catch {
                if (!disposed) { setPositions({}); setError("Robot checkpoint positions are unavailable."); }
            }
        }
        void refresh();
        const timer = window.setInterval(() => void refresh(), 3000);
        return () => { disposed = true; window.clearInterval(timer); };
    }, [robots, mapRevision]);
    const points = warehouseLayout.flatMap((row, y) => row.flatMap((cell, x) =>
        cell.type === "qr" && cell.arucoId != null ? [{ id: cell.arucoId, x, y }] : []));
    const markers = robots.flatMap(robot => {
        const point = points.find(p => p.id === positions[robot.robotCode]);
        if (!point) return [];
        return [{ id: robot.robotCode, x: point.x, y: point.y, tx: point.x, ty: point.y,
            battery: robot.batteryPercent ?? 0, mqtt: robot.connectionStatus as "ONLINE" | "OFFLINE",
            missionId: null, color: robot.connectionStatus === "ONLINE" ? "#22d3ee" : "#ef4444" }];
    });
    return <div className="h-full flex flex-col">
        <p className="px-3 py-2 text-xs" role={mapError || error ? "alert" : "status"}>
            {mapError || error || `Last confirmed QR checkpoints · map ${mapRevision ?? "loading"} · ${markers.length} located robots`}
        </p>
        <div className="flex-1 min-h-0"><WarehouseGrid robots={markers} grid={warehouseLayout} links={links} animateRobots={false} /></div>
    </div>;
}
