import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { apiBaseUrl, apiClient, tokenStorage } from "../api/client";

export interface RobotPose {
    robotCode: string; runId: string; sequence: number; mapRevision: number;
    source: "SIMULATION_GROUND_TRUTH" | "ARUCO_IMU_ODOMETRY"; frame: "GRID_COL_ROW";
    x: number; y: number; yaw: number; capturedAtUtc: string;
}
export function acceptPose(old: RobotPose | undefined, next: RobotPose, revision: number | null): boolean {
    if (next.mapRevision !== revision || next.frame !== "GRID_COL_ROW" || !["SIMULATION_GROUND_TRUTH", "ARUCO_IMU_ODOMETRY"].includes(next.source) ||
        !Number.isFinite(next.x) || !Number.isFinite(next.y) || !Number.isFinite(next.yaw) || !Number.isFinite(Date.parse(next.capturedAtUtc))) return false;
    return !old || next.runId === old.runId && next.sequence > old.sequence ||
        next.runId !== old.runId && Date.parse(next.capturedAtUtc) > Date.parse(old.capturedAtUtc);
}
export default function useRobotPoses(revision: number | null) {
    const [poses, setPoses] = useState<Record<string, RobotPose>>({});
    const [telemetry, setTelemetry] = useState<Record<string, { encoderDistanceM: number; receivedAt: string }>>({});
    const [connected, setConnected] = useState(false);
    const [clockOffset, setClockOffset] = useState(0);
    const [now, setNow] = useState(Date.now());
    useEffect(() => {
        let disposed = false;
        setPoses({});
        setTelemetry({});
        const connection = new signalR.HubConnectionBuilder().withUrl(`${apiBaseUrl}/hubs/robots`,
            { accessTokenFactory: () => tokenStorage.get() ?? "" }).withAutomaticReconnect().build();
        const receive = (pose: RobotPose) => {
            if (disposed) return;
            setPoses(previous => acceptPose(previous[pose.robotCode], pose, revision) ? { ...previous, [pose.robotCode]: pose } : previous);
        };
        async function snapshot() {
            try {
                const { data } = await apiClient.get<{ poses: RobotPose[]; serverTimeUtc: string }>("/api/v1/simulation/poses");
                if (disposed) return;
                if (data.serverTimeUtc) setClockOffset(Date.parse(data.serverTimeUtc) - Date.now());
                for (const pose of data.poses ?? []) receive(pose);
            } catch { /* retain last measured position and let its freshness expire */ }
        }
        connection.on("RobotPoseUpdated", receive);
        connection.on("RobotTelemetryUpdated", (event: {robotCode: string; encoderDistanceM: number | null; receivedAt: string}) => {
            if (!disposed && Number.isFinite(event.encoderDistanceM) && event.encoderDistanceM! >= 0)
                setTelemetry(old => ({...old, [event.robotCode]: {encoderDistanceM: event.encoderDistanceM!, receivedAt: event.receivedAt}}));
        });
        connection.onreconnecting(() => { if (!disposed) setConnected(false); });
        connection.onclose(() => { if (!disposed) setConnected(false); });
        connection.onreconnected(() => { if (!disposed) { setConnected(true); void snapshot(); } });
        let starting: Promise<void> | null = null;
        const start = () => {
            if (disposed || starting || connection.state !== signalR.HubConnectionState.Disconnected) return;
            starting = connection.start().then(() => { if (!disposed) setConnected(true); }).catch(() => {}).finally(() => { starting = null; });
        };
        start(); void snapshot();
        const timer = window.setInterval(() => { start(); void snapshot(); }, 10000);
        return () => { disposed = true; window.clearInterval(timer); void (starting ?? Promise.resolve()).finally(() => connection.stop()); };
    }, [revision]);
    useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 500); return () => window.clearInterval(timer); }, []);
    return { poses, telemetry, connected, serverNow: now + clockOffset };
}
