import { apiClient } from "../client";

export interface InterventionRow {
  taskId: number; robotId: number; robotCode: string; taskTrackingCode: string;
  taskStatus: string; operationalStatus: string; connectionStatus: string; missionStatus: string;
  intervention: { state: string; paused: boolean; commandAcknowledged: boolean } | null;
}
export async function getInterventions(): Promise<InterventionRow[]> {
  return (await apiClient.get<InterventionRow[]>("/api/v1/task-interventions")).data;
}
export async function intervene(taskId: number, action: string, reason: string,
  physicalSafetyConfirmed = false, loadRemovedConfirmed = false): Promise<string> {
  const { data } = await apiClient.post(`/api/v1/task-interventions/${taskId}/${action}`,
    { reason, physicalSafetyConfirmed, loadRemovedConfirmed });
  return data.message;
}
