import { apiClient } from "../client";

const ROOT = "/api/v1/warehouse-maps";

export interface WarehouseOption {
    warehouseId: number;
    warehouseCode: string;
    warehouseName: string;
}

export interface ArucoMapMarker {
    arucoId: number;
    cellCode: string;
    gridRow: number;      // A = 0, B = 1, ...
    gridColumn: number;   // 1, 2, 3, ...
    updatedAt: string;
}

export interface LiveRobotMapPosition {
    robotId: number;
    robotCode: string;
    connectionStatus: string;
    operationalStatus: string;
    arucoId: number | null;
    cellCode: string | null;
    gridRow: number | null;
    gridColumn: number | null;
    seenAt: string | null;
}

export interface WarehouseMapSnapshot extends WarehouseOption {
    markers: ArucoMapMarker[];
    robots: LiveRobotMapPosition[];
}

export interface RobotPositionUpdatedEvent {
    robotId: number;
    robotCode: string;
    warehouseId: number;
    arucoId: number;
    cellCode: string;
    gridRow: number;
    gridColumn: number;
    seenAt: string;
}

export async function listMapWarehouses(): Promise<WarehouseOption[]> {
    const { data } = await apiClient.get<WarehouseOption[]>(`${ROOT}/warehouses`);
    return data;
}

export async function getWarehouseMap(warehouseId: number): Promise<WarehouseMapSnapshot> {
    const { data } = await apiClient.get<WarehouseMapSnapshot>(`${ROOT}/${warehouseId}`);
    return data;
}

export async function saveArucoMapping(
    warehouseId: number,
    arucoId: number,
    gridRow: number,
    gridColumn: number,
): Promise<void> {
    await apiClient.put(`${ROOT}/${warehouseId}/markers/${arucoId}`, {
        gridRow,
        gridColumn,
    });
}

export async function removeArucoMapping(warehouseId: number, arucoId: number): Promise<void> {
    await apiClient.delete(`${ROOT}/${warehouseId}/markers/${arucoId}`);
}
