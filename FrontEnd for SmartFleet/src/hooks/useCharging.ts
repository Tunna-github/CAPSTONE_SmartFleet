import { useEffect,useState } from "react";
import { apiClient } from "../api/client";
export interface ChargingJob { id:string; robotId:number; robotCode:string; sourceMissionId:number; stationId:number|null; state:string; nextWaypointIndex:number; mapRevision:number; reason:string|null; batteryPercent:number|null; routeJson:string|null; hasActiveAssignment?:boolean; }
export default function useCharging(enabled=true) {
  const [jobs,setJobs]=useState<ChargingJob[]>([]);
  const [available,setAvailable]=useState(false);
  const [error,setError]=useState("");
  useEffect(()=>{
    if(!enabled) return;
    let disposed=false;
    const refresh=async()=>{try { const {data}=await apiClient.get<{enabled:boolean;jobs:ChargingJob[]}>("/api/v1/charging"); if(!disposed) {setAvailable(!!data.enabled);setJobs(Array.isArray(data.jobs)?data.jobs:[]);setError("");} }catch {if(!disposed)setError("Charging status unavailable; do not assume a berth is free.");}};
    void refresh();const timer=window.setInterval(()=>void refresh(),3000);
    return ()=>{disposed=true;window.clearInterval(timer);};
  },[enabled]);
  return {jobs,available,error};
}
