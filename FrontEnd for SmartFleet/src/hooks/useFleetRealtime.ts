import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { tokenStorage } from "../api/client";
import type { RobotInfo } from "../api/modules/robots";

/** Server event emitted after an MQTT battery sample has been saved in PostgreSQL. */
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

/** Other robot changes (dispatch, maintenance, online/offline) from the same hub. */
export interface RobotUpdated {
    robotId: number;
    robotCode: string;
    batteryPercent?: number | null;
    batteryVoltage?: number | null;
    connectionStatus?: string;
    operationalStatus?: RobotInfo["operationalStatus"];
    isActive?: boolean;
}

export type FleetConnectionStatus =
    | "CONNECTING"
    | "CONNECTED"
    | "RECONNECTING"
    | "DISCONNECTED";

/** One SignalR connection while the component is mounted; no REST polling. */
export function useFleetRealtime(
    onBattery: (event: RobotBatteryUpdated) => void,
    onRobot: (event: RobotUpdated) => void,
    onReconnect: () => Promise<void>,
): FleetConnectionStatus {
    const [status, setStatus] = useState<FleetConnectionStatus>("CONNECTING");

    useEffect(() => {
        let disposed = false;
        let retryTimer: ReturnType<typeof setTimeout> | undefined;

        const baseUrl = (import.meta.env.VITE_API_URL || "https://localhost:7166")
            .replace(/\/+$/, "");

        const connection = new signalR.HubConnectionBuilder()
            .withUrl(`${baseUrl}/hubs/robots`, {
                accessTokenFactory: () => tokenStorage.get() ?? "",
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        connection.on("RobotBatteryUpdated", (event: RobotBatteryUpdated) => {
            if (!disposed && event && typeof event.robotId === "number") {
                onBattery(event);
            }
        });

        connection.on("RobotUpdated", (event: RobotUpdated) => {
            if (!disposed && event && typeof event.robotId === "number") {
                onRobot(event);
            }
        });

        connection.onreconnecting(() => {
            if (!disposed) setStatus("RECONNECTING");
        });

        connection.onreconnected(() => {
            if (disposed) return;
            setStatus("CONNECTED");
            // Catch up on changes missed while the socket was disconnected.
            void onReconnect().catch((err: unknown) => {
                console.warn("SmartFleet snapshot refresh failed after reconnect", err);
            });
        });

        async function start(): Promise<void> {
            if (disposed) return;
            try {
                await connection.start();
                if (disposed) {
                    await connection.stop();
                    return;
                }
                setStatus("CONNECTED");
            } catch (err) {
                if (disposed) return;
                setStatus("DISCONNECTED");
                console.warn("SmartFleet SignalR unavailable; retrying", err);
                retryTimer = setTimeout(() => void start(), 5000);
            }
        }

        connection.onclose(() => {
            if (disposed) return;
            setStatus("DISCONNECTED");
            // The built-in reconnect retries are finite; retry again if all failed.
            retryTimer = setTimeout(() => void start(), 5000);
        });

        void start();

        return () => {
            disposed = true;
            if (retryTimer !== undefined) clearTimeout(retryTimer);
            void connection.stop();
        };
    }, [onBattery, onRobot, onReconnect]);

    return status;
}
