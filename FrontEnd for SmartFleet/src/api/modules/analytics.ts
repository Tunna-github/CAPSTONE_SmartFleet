import {useEffect,useState} from "react";
import {apiClient} from "../client";

export interface AnalyticsReport {
    window:{from:string;to:string;generatedAt:string;timeZone:string;warehouseId:number|null;robotId:number|null};
    fleet:{registered:number;active:number;online:number;offline:number;available:number;assigned:number;maintenance:number;lowBattery:number;unknownBattery:number};
    tasks:{created:number;completed:number;failed:number;cancelled:number;other:number;completedInPeriod:number;liveQueue:number;liveExecuting:number;successRatePercent:number|null;averageCreateToAssignmentSeconds:number|null;averageCompletionSeconds:number|null;completionDurationSamples:number;statusCounts:Record<string,number>};
    missions:{created:number;completed:number;failed:number;cancelled:number;failedWithActiveHold:number;averageExecutionSeconds:number|null;executionDurationSamples:number};
    maintenance:{created:number;completedInPeriod:number;openNow:number;incidentsCreated:number;unresolvedIncidentsNow:number};
    assignmentUtilizationPercent:number|null;
    robotUtilization:{robotId:number;robotCode:string;warehouseId:number;isActive:boolean;assignments:number;completedMissions:number;assignedSeconds:number;registeredSeconds:number;utilizationPercent:number|null}[];
    daily:{date:string;createdTasks:number;completedTasks:number}[];
    warehouses:{warehouseId:number;code:string;name:string}[];
    robots:{robotId:number;warehouseId:number;robotCode:string}[];
}
export function vietnamDay() {
    const parts=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Ho_Chi_Minh',year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(new Date());
    return ['year','month','day'].map(t=>parts.find(p=>p.type===t)!.value).join('-');
}
export function useAnalytics(query:string) {
    const [report,setReport]=useState<AnalyticsReport|null>(null),[error,setError]=useState('');
    useEffect(()=>{
        let disposed=false,running=false;const controller=new AbortController();setReport(null);setError('');
        const load=async()=>{
            if(running)return;running=true;
            try{const {data}=await apiClient.get<AnalyticsReport>(`/api/v1/analytics?${query}`,{signal:controller.signal});
                if(!data?.fleet||!Array.isArray(data.daily))throw Error('Invalid analytics response');
                if(!disposed){setReport(data);setError('');}}
            catch(e){if(!disposed)setError((e as Error).message);}finally{running=false;}
        };
        void load();const interval=window.setInterval(()=>{if(document.visibilityState==='visible')void load();},5000);
        return()=>{disposed=true;controller.abort();window.clearInterval(interval);};
    },[query]);
    return {report,error};
}
export const metric=(value:number|null|undefined,suffix='')=>value==null?'N/A':`${value.toLocaleString(undefined,{maximumFractionDigits:2})}${suffix}`;
