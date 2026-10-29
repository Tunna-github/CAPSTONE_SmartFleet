
import { apiClient } from "../client";
import { ENDPOINTS } from "../endpoints";
import type { ApiResponse } from "../types";
import type {
    Priority,
    Task,
    TaskStatus,
} from "../../data/mockData";

type ApiResult<T> = T | ApiResponse<T>;

export type DispatchMode = "AUTO" | "MANUAL";

// =====================================================
// CREATE TASK REQUEST
// =====================================================

export interface CreateTransportTaskPayload {
    warehouseId: number;
    pickupStationId: number;
    deliveryStationId: number;
    priorityLevel: number;
    packageCode: string | null;
    itemDescription: string | null;
    payloadWeightKg: number | null;
    scheduledTime: string | null;

    // NEW
    dispatchMode?: DispatchMode;
}

// =====================================================
// BACKEND TASK MODEL
// =====================================================

interface TransportTaskApiModel {
    taskId?: number | string;
    taskTrackingCode?: string;
    pickupStationName?: string;
    deliveryStationName?: string;
    priorityLevel?: number | string;
    taskStatus?: string;
    packageCode?: string;
    itemDescription?: string;
    createdAt?: string;
    assignedRobotId?: string | null;
    robotId?: string | null;
}

export interface AvailableRobot {
    robotId: number | null;
    operationalStatus: string | null;
    id: string;
    model: string;
    battery: number | null;
    mqtt: string | null;
    zone: string | null;
    raw: Record<string, unknown>;
}

// =====================================================
// RESPONSE HELPERS
// =====================================================

function unwrapApiResponse<T>(payload: ApiResult<T>): T {
    if (
        payload &&
        typeof payload === "object" &&
        !Array.isArray(payload) &&
        "data" in payload
    ) {
        return payload.data as T;
    }

    return payload as T;
}

function normalizePriority(value: unknown): Priority {
    if (typeof value === "number") {
        if (value <= 1) return "HIGH";
        if (value === 2) return "MEDIUM";
        return "LOW";
    }

    const normalized = String(value ?? "").toUpperCase();

    if (normalized === "1" || normalized === "HIGH") {
        return "HIGH";
    }

    if (normalized === "2" || normalized === "MEDIUM") {
        return "MEDIUM";
    }

    if (normalized === "3" || normalized === "LOW") {
        return "LOW";
    }

    return "MEDIUM";
}

function normalizeStatus(value: unknown): TaskStatus {
    const normalized = String(value ?? "").toUpperCase();

    if (
        normalized === "PENDING" ||
        normalized === "QUEUED" ||
        normalized === "ASSIGNED" ||
        normalized === "EXECUTING" ||
        normalized === "COMPLETED" ||
        normalized === "CANCELLED"
    ) {
        return normalized;
    }

    return "PENDING";
}

function normalizeCreated(value: unknown): string {
    if (!value) {
        return new Date().toLocaleTimeString("en-GB", {
            hour: "2-digit",
            minute: "2-digit",
        });
    }

    const parsed = new Date(String(value));

    if (!Number.isNaN(parsed.getTime())) {
        return parsed.toLocaleTimeString("en-GB", {
            hour: "2-digit",
            minute: "2-digit",
        });
    }

    return String(value);
}

function normalizePackageInfo(
    item: TransportTaskApiModel
): string {
    const packageCode = item.packageCode?.trim();
    const description = item.itemDescription?.trim();

    if (packageCode && description) {
        return `${packageCode} - ${description}`;
    }

    return packageCode || description || "No package details";
}

function formatTaskCode(
    item: TransportTaskApiModel
): string {
    const trackingCode =
        item.taskTrackingCode?.trim() || "";

    const shortCode =
        /^(?:T)?(\d{1,3})$/i.exec(trackingCode);

    if (shortCode) {
        return `T${shortCode[1].padStart(3, "0")}`;
    }

    const backendId =
        String(item.taskId ?? "").trim();

    if (/^\d+$/.test(backendId)) {
        return `T${backendId
            .replace(/^0+(?=\d)/, "")
            .padStart(3, "0")}`;
    }

    return trackingCode || backendId || "Unknown task";
}

function normalizeTask(
    item: TransportTaskApiModel
): Task {
    const robotId =
        item.assignedRobotId ??
        item.robotId ??
        null;

    return {
        taskId: item.taskId,
        id: formatTaskCode(item),
        pickup:
            item.pickupStationName || "Unknown pickup",
        delivery:
            item.deliveryStationName || "Unknown delivery",
        packageInfo: normalizePackageInfo(item),
        priority: normalizePriority(item.priorityLevel),
        robotId: robotId ? String(robotId) : null,
        status: normalizeStatus(item.taskStatus),
        created: normalizeCreated(item.createdAt),
    };
}

function normalizeAvailableRobot(
    item: Record<string, unknown>
): AvailableRobot {
    return {
        robotId:
            typeof item.robotId === "number"
                ? item.robotId
                : null,

        operationalStatus:
            item.operationalStatus
                ? String(item.operationalStatus)
                : null,

        id: String(
            item.robotCode ??
            item.robotId ??
            item.id ??
            ""
        ),

        model: String(
            item.model ??
            item.robotModel ??
            item.name ??
            ""
        ),

        battery:
            typeof item.batteryPercent === "number"
                ? item.batteryPercent
                : null,

        mqtt:
            item.connectionStatus
                ? String(item.connectionStatus)
                : null,

        zone: item.zone ? String(item.zone) : null,
        raw: item,
    };
}

// =====================================================
// GET ALL TASKS
// =====================================================

export async function getTransportTasks(): Promise<Task[]> {
    const { data } = await apiClient.get<
        ApiResult<TransportTaskApiModel[]>
    >(ENDPOINTS.transportTasks.list);

    const items = unwrapApiResponse(data);

    return items.map(normalizeTask);
}

// =====================================================
// CREATE TASK - AUTO OR MANUAL
// =====================================================

export async function createTransportTask(
    payload: CreateTransportTaskPayload
): Promise<Task> {
    const { data } = await apiClient.post<
        ApiResult<TransportTaskApiModel>
    >(
        ENDPOINTS.transportTasks.create,
        {
            ...payload,
            dispatchMode: payload.dispatchMode ?? "MANUAL",
        }
    );

    return normalizeTask(
        unwrapApiResponse(data)
    );
}

// =====================================================
// DELETE TASK
// =====================================================

export async function deleteTransportTask(
    taskId: number | string
): Promise<void> {
    await apiClient.delete(
        ENDPOINTS.transportTasks.delete(taskId)
    );
}

// =====================================================
// GET TASK BY ID
// =====================================================

export async function getTransportTaskById(
    taskId: number | string
): Promise<Task> {
    const { data } = await apiClient.get<
        ApiResult<TransportTaskApiModel>
    >(ENDPOINTS.transportTasks.detail(taskId));

    return normalizeTask(
        unwrapApiResponse(data)
    );
}

// =====================================================
// GET ELIGIBLE ROBOTS FOR MANUAL DISPATCH
// =====================================================

export async function getAvailableRobotsForTask(
    taskId: number | string
): Promise<AvailableRobot[]> {
    const { data } = await apiClient.get<
        ApiResult<Record<string, unknown>[]>
    >(
        ENDPOINTS.transportTasks.availableRobots(taskId)
    );

    return unwrapApiResponse(data)
        .map(normalizeAvailableRobot);
}

// =====================================================
// MANUAL ASSIGN RESPONSE
// =====================================================

export interface ManualAssignTaskResponse {
    taskId: number;
    taskTrackingCode: string | null;
    robotId: number;
    robotCode: string | null;
    taskStatus: string | null;
    robotStatus: string | null;
    missionId: number;
    missionCode: string | null;
    missionStatus: string | null;
    message: string | null;
}

// =====================================================
// ASSIGN SELECTED ROBOT MANUALLY
// =====================================================

export async function assignRobotToTask(
    taskId: number | string,
    robotId: number
): Promise<ManualAssignTaskResponse> {
    const { data } = await apiClient.post<
        ApiResult<ManualAssignTaskResponse>
    >(
        ENDPOINTS.transportTasks.assign(taskId),
        { robotId }
    );

    return unwrapApiResponse(data);
}
