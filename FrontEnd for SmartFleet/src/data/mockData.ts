export type Priority = "HIGH" | "MEDIUM" | "LOW";
export type TaskStatus = "PENDING" | "QUEUED" | "ASSIGNED" | "EXECUTING" | "COMPLETED" | "CANCELLED";
export type MQTTStatus = "ONLINE" | "OFFLINE";

export interface Task {
    id: string; pickup: string; delivery: string; packageInfo: string;
    priority: Priority; robotId: string | null; status: TaskStatus; created: string;
}

export interface Robot {
    id: string; missionId: string | null; battery: number;
    mqtt: MQTTStatus; maintenance: boolean; model: string; zone: string;
}

export interface LogEntry {
    id: number; time: string; level: "INFO" | "WARN" | "ERROR" | "SUCCESS";
    robot: string; message: string;
}

export interface RobotPos {
    id: string; x: number; y: number; tx: number; ty: number;
    battery: number; mqtt: MQTTStatus; missionId: string | null; color: string;
}

export const TASKS: Task[] = [
    { id: "TSK-0041", pickup: "Station A-01", delivery: "Bay D-12", packageInfo: "Industrial Parts × 3", priority: "HIGH", robotId: "AMR-002", status: "EXECUTING", created: "09:14" },
    { id: "TSK-0042", pickup: "Station B-03", delivery: "Bay C-08", packageInfo: "Electronics Box", priority: "HIGH", robotId: "AMR-005", status: "ASSIGNED", created: "09:18" },
    { id: "TSK-0043", pickup: "Station A-02", delivery: "Bay E-01", packageInfo: "Raw Material Pallet", priority: "MEDIUM", robotId: null, status: "QUEUED", created: "09:22" },
    { id: "TSK-0044", pickup: "Receiving Dock", delivery: "Bay B-07", packageInfo: "Consumer Goods × 12", priority: "LOW", robotId: null, status: "PENDING", created: "09:25" },
    { id: "TSK-0045", pickup: "Station C-01", delivery: "Shipping Zone", packageInfo: "Finished Goods Batch", priority: "MEDIUM", robotId: "AMR-001", status: "COMPLETED", created: "08:55" },
    { id: "TSK-0046", pickup: "Station B-02", delivery: "Bay A-04", packageInfo: "Chemical Drums × 2", priority: "HIGH", robotId: null, status: "PENDING", created: "09:30" },
    { id: "TSK-0047", pickup: "Receiving Dock", delivery: "Storage F-03", packageInfo: "Spare Components", priority: "LOW", robotId: "AMR-004", status: "EXECUTING", created: "09:05" },
    { id: "TSK-0048", pickup: "Station A-03", delivery: "Bay C-11", packageInfo: "Assembly Kit #7", priority: "MEDIUM", robotId: null, status: "QUEUED", created: "09:31" },
];

export const ROBOTS: Robot[] = [
    { id: "AMR-001", missionId: "TSK-0045", battery: 87, mqtt: "ONLINE", maintenance: false, model: "FleetBot X200", zone: "Zone A" },
    { id: "AMR-002", missionId: "TSK-0041", battery: 62, mqtt: "ONLINE", maintenance: false, model: "FleetBot X200", zone: "Zone D" },
    { id: "AMR-003", missionId: null, battery: 12, mqtt: "OFFLINE", maintenance: true, model: "FleetBot X100", zone: "Charging Bay" },
    { id: "AMR-004", missionId: "TSK-0047", battery: 45, mqtt: "ONLINE", maintenance: false, model: "FleetBot X300", zone: "Zone F" },
    { id: "AMR-005", missionId: "TSK-0042", battery: 91, mqtt: "ONLINE", maintenance: false, model: "FleetBot X300", zone: "Zone C" },
    { id: "AMR-006", missionId: null, battery: 8, mqtt: "ONLINE", maintenance: false, model: "FleetBot X100", zone: "Charging Bay" },
    { id: "AMR-007", missionId: null, battery: 100, mqtt: "ONLINE", maintenance: false, model: "FleetBot X200", zone: "Idle — Zone B" },
    { id: "AMR-008", missionId: null, battery: 33, mqtt: "OFFLINE", maintenance: true, model: "FleetBot X100", zone: "Maintenance Bay" },
];

export const INITIAL_LOGS: LogEntry[] = [
    { id: 1, time: "09:31:52", level: "ERROR", robot: "AMR-003", message: "Heartbeat lost — no response for 4m 12s" },
    { id: 2, time: "09:31:44", level: "WARN", robot: "AMR-003", message: "Battery critical: 12% — return to dock" },
    { id: 3, time: "09:31:30", level: "WARN", robot: "AMR-006", message: "Battery critical: 8% — mission suspended" },
    { id: 4, time: "09:31:11", level: "INFO", robot: "AMR-002", message: "Position update: Zone D → aisle 4" },
    { id: 5, time: "09:30:58", level: "SUCCESS", robot: "AMR-001", message: "TSK-0045 completed — delivered to Bay B-07" },
    { id: 6, time: "09:30:44", level: "INFO", robot: "AMR-005", message: "TSK-0042 assigned — navigating to B-03" },
    { id: 7, time: "09:30:22", level: "WARN", robot: "AMR-008", message: "Obstacle detected — path replanning…" },
    { id: 8, time: "09:30:09", level: "INFO", robot: "AMR-007", message: "Idle — awaiting next task assignment" },
    { id: 9, time: "09:29:55", level: "SUCCESS", robot: "AMR-004", message: "TSK-0047 pickup confirmed at Dock" },
    { id: 10, time: "09:29:33", level: "INFO", robot: "AMR-002", message: "Speed reduced — congestion Zone D" },
];

export const NEW_LOGS: Omit<LogEntry, "id">[] = [
    { time: "", level: "INFO", robot: "AMR-001", message: "Position telemetry OK — Zone A aisle 2" },
    { time: "", level: "WARN", robot: "AMR-006", message: "Battery 7% — emergency dock return initiated" },
    { time: "", level: "SUCCESS", robot: "AMR-005", message: "Pickup confirmed at Station B-03" },
    { time: "", level: "ERROR", robot: "AMR-003", message: "MQTT DISCONNECT — broker timeout" },
    { time: "", level: "INFO", robot: "AMR-007", message: "TSK-0049 assigned — navigating to A-02" },
];

export const INIT_ROBOTS: RobotPos[] = [
    { id: "AMR-001", x: 4, y: 7, tx: 4, ty: 14, battery: 87, mqtt: "ONLINE", missionId: "TSK-0045", color: "#22d3ee" },
    { id: "AMR-002", x: 20, y: 3, tx: 4, ty: 14, battery: 62, mqtt: "ONLINE", missionId: "TSK-0041", color: "#22d3ee" },
    { id: "AMR-003", x: 13, y: 16, tx: 13, ty: 16, battery: 12, mqtt: "OFFLINE", missionId: null, color: "#ef4444" },
    { id: "AMR-004", x: 18, y: 7, tx: 20, ty: 14, battery: 45, mqtt: "ONLINE", missionId: "TSK-0047", color: "#a78bfa" },
    { id: "AMR-005", x: 12, y: 4, tx: 12, ty: 7, battery: 91, mqtt: "ONLINE", missionId: "TSK-0042", color: "#22d3ee" },
    { id: "AMR-006", x: 15, y: 16, tx: 15, ty: 16, battery: 8, mqtt: "ONLINE", missionId: null, color: "#f59e0b" },
    { id: "AMR-007", x: 6, y: 11, tx: 6, ty: 7, battery: 100, mqtt: "ONLINE", missionId: null, color: "#34d399" },
];

export const DELIVERY_OPTIONS = ["Bay A-04", "Bay B-07", "Bay C-08", "Bay C-11", "Bay D-12", "Bay E-01", "Storage F-03", "Shipping Zone", "Receiving Dock"];
export const PICKUP_OPTIONS = ["Station A-01", "Station A-02", "Station A-03", "Station B-02", "Station B-03", "Station C-01", "Receiving Dock"];