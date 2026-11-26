import { useCallback, useEffect, useState, type FormEvent } from "react";
import { apiClient } from "../api/client";

interface RegisteredRobot {
    robotId:number; warehouseId:number; robotCode:string; robotType:string; model:string; controlSoftware:string;
    robotAgentVersion:string|null; ipAddress:string|null; macAddress:string|null; mqttClientIdentifier:string;
    maxPayloadWeightKg:number; isActive:boolean; registryRevision:number; connectionStatus:string; operationalStatus:string;
}
interface Registry {robots:RegisteredRobot[]; warehouses:{warehouseId:number;code:string;name:string;isActive:boolean}[];simulationRegistrationAllowed:boolean}
export default function RobotRegistryPage() {
    const [data,setData]=useState<Registry|null>(null),[editing,setEditing]=useState<Partial<RegisteredRobot>|null>(null);
    const [error,setError]=useState(""),[message,setMessage]=useState(""),[busy,setBusy]=useState(false),[search,setSearch]=useState("");
    const reload=useCallback(async()=>{try{const {data}=await apiClient.get<Registry>("/api/v1/robot-registry");setData(data);setError("");}catch(e){setError((e as Error).message);}},[]);
    useEffect(()=>{void reload();},[reload]);
    const field="w-full rounded-lg border p-2 bg-[var(--surface-3)] text-[var(--text-primary)]";
    async function save(event:FormEvent<HTMLFormElement>) {
        event.preventDefault();if(!editing||busy)return;setBusy(true);setError("");setMessage("");
        const values=Object.fromEntries(new FormData(event.currentTarget));
        const payload={...values,warehouseId:Number(values.warehouseId),maxPayloadWeightKg:Number(values.maxPayloadWeightKg),isActive:values.isActive==="on",registryRevision:editing.registryRevision??0};
        try{await apiClient.request({url:`/api/v1/robot-registry${editing.robotId?`/${editing.robotId}`:""}`,method:editing.robotId?"PUT":"POST",data:payload});setEditing(null);setMessage("Robot configuration saved. Connection and battery are reported by the device.");await reload();}
        catch(e){setError((e as Error).message);}finally{setBusy(false);}
    }
    const input=(name:keyof RegisteredRobot,label:string,maxLength:number,required=true)=><label className="block text-sm" key={name}>{label}<input name={name} className={field} maxLength={maxLength} required={required} readOnly={!!editing?.robotId&&['robotCode','mqttClientIdentifier'].includes(name)} defaultValue={String(editing?.[name]??"")}/></label>;
    return <main className="sf-data-page h-full overflow-y-auto p-6 space-y-5 text-[var(--text-primary)]">
        <h1 className="text-2xl font-bold">Robot registration &amp; configuration</h1>
        <p>Register a device identity and its warehouse. New robots are OFFLINE with unknown battery until the device sends telemetry. Registration does not create a robot in Webots.</p>
        {error&&<p role="alert" className="border border-red-500 rounded p-3 text-red-500">{error}</p>}{message&&<p role="status" className="border rounded p-3">{message}</p>}
        <div className="flex flex-wrap gap-3"><button className="border rounded px-4 py-2" disabled={!data} onClick={()=>{setEditing({robotType:'PHYSICAL',model:'JetRacer',controlSoftware:'NVIDIA_JETRACER_PYTHON',maxPayloadWeightKg:10,isActive:true});setError("");setMessage("");}}>Register robot</button><button className="border rounded px-4 py-2" onClick={()=>void reload()}>Reload registry</button><input aria-label="Search registry" placeholder="Robot code or model" value={search} onChange={e=>setSearch(e.target.value)} className={field+" max-w-xs"}/></div>
        {editing&&<form aria-label="Robot registration editor" key={editing.robotId??"new"} onSubmit={save} className="rounded-xl border p-5 grid md:grid-cols-2 gap-4">
            <h2 className="md:col-span-2 font-bold">{editing.robotId?`Configure ${editing.robotCode}`:"Register a new robot"}</h2>
            {input('robotCode','Robot code',30)}
            <label>Warehouse<select aria-label="Warehouse" name="warehouseId" className={field} required defaultValue={editing.warehouseId??""}><option value="">Select warehouse</option>{data?.warehouses.filter(w=>w.isActive||w.warehouseId===editing.warehouseId).map(w=><option key={w.warehouseId} value={w.warehouseId}>{w.code} — {w.name}{!w.isActive?' (inactive)':''}</option>)}</select></label>
            <label>Robot type<select className={field} name="robotType" defaultValue={editing.robotType} onChange={e=>setEditing({...editing,robotType:e.target.value})}>{['PHYSICAL',...(data?.simulationRegistrationAllowed||editing.robotType==='SIMULATION'?['SIMULATION']:[])].map(t=><option key={t} disabled={!!editing.robotId&&editing.robotType!==t}>{t}</option>)}</select></label>
            {input('mqttClientIdentifier','MQTT client identifier',100)}{input('model','Model',80)}{input('controlSoftware','Control software',80)}
            {input('robotAgentVersion','Agent version (optional)',40,false)}{input('ipAddress','IP address (optional)',45,false)}{input('macAddress','MAC address (optional, AA:BB:CC:DD:EE:FF)',17,false)}
            <label>Maximum payload (kg)<input className={field} name="maxPayloadWeightKg" type="number" min="0.01" max="999999.99" step="0.01" required defaultValue={editing.maxPayloadWeightKg}/></label>
            <label><input type="checkbox" name="isActive" defaultChecked={editing.isActive!==false}/> Active registration</label>
            <p className="text-sm md:col-span-2">Device code/type/MQTT registration identity stay fixed after registration. Disconnect the device and resolve active work before reconfiguration. History is retained; deactivate instead of deleting. Configure the robot agent with the registered robot code; existing device transport settings remain in effect.</p>
            <div className="flex gap-3"><button disabled={busy} className="bg-cyan-700 text-white rounded px-4 py-2">Save robot</button><button disabled={busy} type="button" className="border rounded px-4 py-2" onClick={()=>setEditing(null)}>Close editor</button></div>
        </form>}
        {!data&&!error&&<p role="status">Loading robot registry…</p>}
        <div className="overflow-x-auto"><table className="w-full text-sm text-left"><thead><tr><th>Robot / Model</th><th>Warehouse / Type</th><th>MQTT identity</th><th>Payload</th><th>Registration / Runtime</th><th>Manage</th></tr></thead><tbody>
            {data?.robots.filter(r=>`${r.robotCode} ${r.model}`.toLowerCase().includes(search.toLowerCase())).map(r=><tr key={r.robotId} className="border-t"><td className="py-3 pr-3">{r.robotCode}<br/>{r.model}</td><td>{data.warehouses.find(w=>w.warehouseId===r.warehouseId)?.code} / {r.robotType}</td><td>{r.mqttClientIdentifier}</td><td>{r.maxPayloadWeightKg} kg</td><td>{r.isActive?'Active':'Inactive'} · {r.connectionStatus}<br/>{r.operationalStatus}</td><td><button className="rounded border p-2" onClick={()=>{setEditing({...r});setError("");setMessage("");}}>Configure {r.robotCode}</button></td></tr>)}
        </tbody></table></div>
    </main>;
}
