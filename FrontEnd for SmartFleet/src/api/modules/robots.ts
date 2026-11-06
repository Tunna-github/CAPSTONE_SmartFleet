
import { apiClient } from "../client";

export type RobotOperationalStatus =
    | "AVAILABLE"
    | "ERROR"
    | "MAINTENANCE"
    | "BUSY"
    | "CHARGING";

export interface RobotIncidentInfo {
    incidentId: number;
    robotId: number;
    incidentType: string;
    severity: string;
    description: string | null;
    createdAt: string;
}

export interface RobotInfo {
    robotId: number;
    robotCode: string;
    model: string;
    connectionStatus: string;
    operationalStatus: RobotOperationalStatus;
    batteryPercent: number | null;
    isActive: boolean;
    hasActiveAssignment: boolean;
    hasActiveMaintenance: boolean;
    openIncidents: RobotIncidentInfo[];
}

export async function getRobots(): Promise<RobotInfo[]> {
    const { data } = await apiClient.get<RobotInfo[]>(
        "/api/v1/robots"
    );

    return data;
}

export async function changeRobotStatus(
    robotId: number,
    operationalStatus: "AVAILABLE" | "ERROR" | "MAINTENANCE",
    reason: string
): Promise<void> {
    await apiClient.patch(
        `/api/v1/robots/${robotId}/operational-status`,
        {
            operationalStatus,
            reason,
        }
    );
}

export async function resolveRobotIncident(
    robotId: number,
    incidentId: number,
    resolutionNote: string
): Promise<void> {
    await apiClient.post(
        `/api/v1/robots/${robotId}/incidents/${incidentId}/resolve`,
        {
            resolutionNote,
        }
    );
}
