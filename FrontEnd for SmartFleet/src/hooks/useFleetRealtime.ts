import { useEffect, useRef, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { tokenStorage } from "../api/client";
import type { RobotInfo } from "../api/modules/robots";

/** Server event emitted after an MQTT battery sample is saved in PostgreSQL. */
export interface RobotBatteryUpdated {
    robotId: number;
    robotCode: string;
    batteryPercent: number | null;
    batteryVoltage: number | null;
    batteryLevel: "UNKNOWN" | "CRITICAL" | "LOW" | "NORMAL";
    connectionStatus: string;
    operationalStatus: string;
    lastTelemetryTime: string | null;
    isTelemetryStale: boolean;
    updatedAt: string | null;
}

/** Robot changes (dispatch, maintenance, online/offline). */
export interface RobotUpdated {
    robotId: number;
    robotCode: string;
    batteryPercent?: number | null;
    batteryVoltage?: number | null;
    connectionStatus?: string;
    operationalStatus?: RobotInfo["operationalStatus"];
    isActive?: boolean;
}

/** Front LiDAR scan result broadcast by the backend. */
export interface RobotLidarUpdated {
    robotId: number;
    robotCode: string;
    status: "CLEAR" | "DETECTED" | "CRITICAL" | "NO_SCAN_DATA";
    distanceCm: number | null;
    receivedAt: string;
}

export type FleetConnectionStatus =
    | "CONNECTING"
    | "CONNECTED"
    | "RECONNECTING"
    | "DISCONNECTED";

/**
 * One SignalR connection per mounted component; no REST polling.
 * - Receives battery, robot-status and front-LiDAR updates.
 * - Retries initial connection and reconnects after disconnects.
 * - Avoids stopping an in-progress negotiation during React StrictMode cleanup.
 * - Uses the latest callbacks without restarting the connection each render.
 */
export function useFleetRealtime(
    onBattery: (event: RobotBatteryUpdated) => void,
    onRobot: (event: RobotUpdated) => void,
    onReconnect: () => Promise<void>,
    onLidar?: (event: RobotLidarUpdated) => void,
): FleetConnectionStatus {
    const [status, setStatus] = useState<FleetConnectionStatus>("CONNECTING");

    const callbacksRef = useRef({ onBattery, onRobot, onReconnect, onLidar });
    callbacksRef.current = { onBattery, onRobot, onReconnect, onLidar };

    useEffect(() => {
        let disposed = false;
        let retryTimer: ReturnType<typeof setTimeout> | undefined;
        let startPromise: Promise<void> | null = null;

        const baseUrl = (
            import.meta.env.VITE_API_URL || "https://localhost:7166"
        ).replace(/\/+$/, "");

        const connection = new signalR.HubConnectionBuilder()
            .withUrl(`${baseUrl}/hubs/robots`, {
                accessTokenFactory: () => tokenStorage.get() ?? "",
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        connection.on("RobotBatteryUpdated", (event: RobotBatteryUpdated) => {
            if (!disposed && event && typeof event.robotId === "number") {
                callbacksRef.current.onBattery(event);
            }
        });

        connection.on("RobotUpdated", (event: RobotUpdated) => {
            if (!disposed && event && typeof event.robotId === "number") {
                callbacksRef.current.onRobot(event);
            }
        });

        // Important: retain LiDAR event handling when replacing the old hook.
        connection.on("RobotLidarUpdated", (event: RobotLidarUpdated) => {
            if (!disposed && event && typeof event.robotId === "number") {
                callbacksRef.current.onLidar?.(event);
            }
        });

        const scheduleRetry = () => {
            if (disposed || retryTimer !== undefined) return;

            retryTimer = setTimeout(() => {
                retryTimer = undefined;
                void start();
            }, 5000);
        };

        async function start(): Promise<void> {
            if (
                disposed ||
                startPromise !== null ||
                connection.state !== signalR.HubConnectionState.Disconnected
            ) {
                return;
            }

            setStatus("CONNECTING");
            const pending = connection.start();
            startPromise = pending;

            try {
                await pending;
                if (!disposed) setStatus("CONNECTED");
            } catch (error: unknown) {
                if (!disposed) {
                    setStatus("DISCONNECTED");
                    console.warn("SmartFleet SignalR unavailable; retrying", error);
                    scheduleRetry();
                }
            } finally {
                if (startPromise === pending) startPromise = null;
            }
        }

        connection.onreconnecting(() => {
            if (!disposed) setStatus("RECONNECTING");
        });

        connection.onreconnected(() => {
            if (disposed) return;
            setStatus("CONNECTED");

            // Refresh once to recover updates missed during the interruption.
            void callbacksRef.current.onReconnect().catch((error: unknown) => {
                console.warn("SmartFleet snapshot refresh failed", error);
            });
        });

        connection.onclose(() => {
            if (disposed) return;
            setStatus("DISCONNECTED");
            scheduleRetry();
        });

        console.info("[SmartFleet SignalR] Connecting to:", `${baseUrl}/hubs/robots`);
        void start();

        return () => {
            disposed = true;
            if (retryTimer !== undefined) clearTimeout(retryTimer);

            // Wait for the pending start to settle before stopping. Calling
            // stop() while negotiate is in flight creates the StrictMode race.
            const pending = startPromise;
            if (pending !== null) {
                void pending
                    .then(
                        () => connection.stop(),
                        () => connection.stop(),
                    )
                    .catch((error: unknown) => {
                        console.debug("SmartFleet SignalR cleanup", error);
                    });
            } else {
                void connection.stop().catch((error: unknown) => {
                    console.debug("SmartFleet SignalR cleanup", error);
                });
            }
        };
    }, []);

    return status;
}
