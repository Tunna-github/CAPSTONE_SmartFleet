import { apiClient } from "../client";

// Matches SmartFleetBE.Navigation.NavigationModels, NOT a mock dataset.
export type NavigationCellType = "wall" | "floor" | "station" | "qr" | "charge";

export interface NavigationMapCell {
  type: NavigationCellType;
  label: string | null;
  arucoId: number | null;
  isIntersection: boolean;
  markerYawDeg?: number | null;
  markerSizeM?: number | null;
}

export interface NavigationMapDocument {
  revision: number;
  rows: number;
  columns: number;
  cells: NavigationMapCell[][];
  links: string[][];
}

export interface ApproachingRobot {
  robotId: number;
  robotCode: string;
  currentArUcoId: number;
  nextArUcoId: number;
  phase: string;
  lastReportedAtUtc: string;
}

export interface IntersectionTraffic {
  intersectionArUcoId: number;
  activeRobotCode: string | null;
  activeRobotEntered: boolean;
  blockedPendingPhysicalClearance: boolean;
  lastServedRobotId: number;
  approachingCount: number;
  waitingCount: number;
  insideCount: number;
  totalRelevantCount: number;
  waitingRobotCodes: string[];
  approachingRobots: ApproachingRobot[];
  approachingBySourceArUco: Record<string, number>;
}

const BASE = "/api/v1/navigation";

export interface MapReadiness {
  mapRevision: number;
  qrNodeCount: number;
  corridorCount: number;
  readyForRouting: boolean;
  issues: string[];
}

export async function getMapReadiness(): Promise<MapReadiness> {
  const { data } = await apiClient.get<MapReadiness>("/api/v1/demo-routing/map/validation");
  return data;
}

export async function getNavigationMap(): Promise<NavigationMapDocument> {
  const { data } = await apiClient.get<NavigationMapDocument>(`${BASE}/map`);
  return data;
}

/** Uses Backend's optimistic revision check. A stale edit results in HTTP 409. */
export async function putNavigationMap(map: NavigationMapDocument): Promise<NavigationMapDocument> {
  const { data } = await apiClient.put<NavigationMapDocument>(`${BASE}/map`, map);
  return data;
}

export async function getNavigationIntersections(): Promise<IntersectionTraffic[]> {
  const { data } = await apiClient.get<IntersectionTraffic[]>(`${BASE}/intersections`);
  return data;
}

export async function getNavigationIntersection(id: number): Promise<IntersectionTraffic> {
  const { data } = await apiClient.get<IntersectionTraffic>(`${BASE}/intersections/${id}`);
  return data;
}

/** Development-only, Administrator-only. BE skips publishing MQTT navigation commands. */
export async function simulateNavigationScan(
  robotCode: string,
  currentArUcoId: number,
  nextArUcoId: number | null,
): Promise<void> {
  await apiClient.post(
    `${BASE}/robots/${encodeURIComponent(robotCode.trim())}/scan`,
    { currentArUcoId, nextArUcoId },
    { params: { simulate: true } },
  );
}

export function navigationErrorMessage(error: unknown): string {
  if (error && typeof error === "object" && "message" in error) {
    const value = (error as { message?: unknown }).message;
    if (typeof value === "string") return value;
  }
  return "Navigation API request failed.";
}
