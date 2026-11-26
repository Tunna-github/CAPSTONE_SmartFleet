import { useCallback, useEffect, useRef, useState } from "react";
import { apiClient } from "../api/client";
import type { VerificationMission } from "../components/MissionVerificationPanel";

export interface TrackingMission extends VerificationMission {
    mapRevision: number;
    taskId: number;
    robotCode: string;
    pickupArUcoId: number;
    deliveryArUcoId: number;
    lastReachedArUcoId: number | null;
    nextWaypointIndex: number;
    waypoints: { arucoId: number; stage: string; row: number; col: number }[];
}

export default function useMissionWorkspace(visible: boolean) {
    const [missions, setMissions] = useState<TrackingMission[]>([]);
    const [error, setError] = useState("");
    const [updatedAt, setUpdatedAt] = useState(0);
    const [now, setNow] = useState(Date.now());
    const request = useRef<AbortController | null>(null);
    const generation = useRef(0);
    const reload = useCallback(async () => {
        request.current?.abort();
        const controller = new AbortController();
        request.current = controller;
        const version = ++generation.current;
        try {
            const { data } = await apiClient.get<TrackingMission[]>("/api/v1/missions", { signal: controller.signal });
            if (!Array.isArray(data)) throw new Error("Invalid mission response.");
            if (version === generation.current) {
                setMissions(data); setUpdatedAt(Date.now()); setNow(Date.now()); setError("");
            }
        } catch (e) {
            if (!controller.signal.aborted && version === generation.current) {
                setError((e as Error).message || "Mission updates unavailable."); setUpdatedAt(0);
            }
        } finally { if (version === generation.current) request.current = null; }
    }, []);
    useEffect(() => {
        setUpdatedAt(0);
        if (!visible) return;
        void reload();
        const polling = window.setInterval(() => { if (!request.current) void reload(); }, 2000);
        const clock = window.setInterval(() => setNow(Date.now()), 1000);
        return () => {
            window.clearInterval(polling); window.clearInterval(clock);
            generation.current++; request.current?.abort(); request.current = null;
        };
    }, [visible, reload]);
    return { missions, error, reload, fresh: visible && updatedAt > 0 && now - updatedAt < 6000 };
}
