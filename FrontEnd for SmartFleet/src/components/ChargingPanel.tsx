import { useState } from "react";
import { apiClient } from "../api/client";
import { useAuth } from "../context/AuthContext";
import { useCatalog } from "../api/modules/catalog";
import useCharging from "../hooks/useCharging";

export default function ChargingPanel({visible=true}:{visible?:boolean}) {
  const {jobs,available,error}=useCharging(visible);
  const {catalog}=useCatalog();
  const {role}=useAuth();
  const [notes,setNotes]=useState<Record<string,string>>({});
  const [checked,setChecked]=useState<Record<string,boolean>>({});
  const [busy,setBusy]=useState("");const [message,setMessage]=useState("");
  async function control(id:string,resume:boolean,release=false) {
    setBusy(id);setMessage("");
    try {await apiClient.post(`/api/v1/charging/${id}/${release?"release-recovered":resume?"resume":"pause"}`,{reason:notes[id]??"Safety stop requested from live tracking",physicalSafetyConfirmed:!!checked[id]});setMessage(release?"Vacated berth released. Robot safety and Maintenance holds are preserved.":resume?"Recovery requested; berth stays reserved.":"Pause requested. Verify that the robot has stopped.");}
    catch(e) {setMessage((e as {message?:string}).message??"Charging control failed.");}finally{setBusy("");}
  }
  if(!available&&!error)return null;
  return <section className="sf-card p-4 mt-3" aria-label="Charging and return to service"><h2 className="font-semibold">Return to charging &amp; readiness</h2><p className="sf-muted text-xs mt-1">Four separate bays · Finish return, stop safely, battery ≥20% before the next task. Reservations remain until camera-confirmed departure.</p>
    {(error||message)&&<p role="status" className="sf-notice mt-2">{error||message}</p>}
    {jobs.length===0&&<p className="sf-muted text-sm mt-3">No robots returning or charging.</p>}
    {jobs.map(j=><article key={j.id} className="border border-[var(--border-subtle)] rounded-xl p-3 mt-3"><div className="flex gap-2 justify-between"><strong>{j.robotCode}</strong><span className="sf-badge">{j.state.replace(/_/g," ")}</span></div><p className="sf-muted text-xs mt-1">{catalog?.stations.find(s=>s.stationId===j.stationId)?.name??"Waiting for an available bay"} · Battery {j.batteryPercent??"?"}% · after mission #{j.sourceMissionId}</p>
      {j.reason&&<p className="text-xs mt-2">{j.reason}</p>}
      {j.state!=="DEPARTING"&&<button className="sf-button mt-2 !text-red-500" disabled={!!busy} onClick={()=>void control(j.id,false)}>Pause robot</button>}
      {j.state==="DEPARTING"&&<p className="sf-muted text-xs mt-2">{j.hasActiveAssignment ? "Transport task is active: use task Pause/cancellation controls. Bay releases after confirmed exit." : "Interrupted departure: finish load recovery, then Maintenance can release this bay after fresh camera confirmation outside it."}</p>}
      {role==="maintenance"&&["PAUSED","FAILED"].includes(j.state)&&<div className="grid gap-2 mt-2"><label className="sf-label">Recovery reason<input className="sf-input" value={notes[j.id]??""} onChange={e=>setNotes({...notes,[j.id]:e.target.value})}/></label><label className="text-xs"><input type="checkbox" checked={!!checked[j.id]} onChange={e=>setChecked({...checked,[j.id]:e.target.checked})}/> Robot stopped, area inspected, fresh camera localization obtained</label><button className="sf-button" disabled={!!busy||!checked[j.id]||(notes[j.id]?.trim().length??0)<5} onClick={()=>void control(j.id,true)}>Resume safe return</button></div>}
      {role==="maintenance"&&j.state==="DEPARTING"&&j.hasActiveAssignment===false&&<div className="grid gap-2 mt-2"><label className="sf-label">Bay clearance reason<input className="sf-input" value={notes[j.id]??""} onChange={e=>setNotes({...notes,[j.id]:e.target.value})}/></label><label className="text-xs"><input type="checkbox" checked={!!checked[j.id]} onChange={e=>setChecked({...checked,[j.id]:e.target.checked})}/> Robot stopped, load recovered and charging bay physically clear</label><button className="sf-button" disabled={!!busy||!checked[j.id]||(notes[j.id]?.trim().length??0)<5} onClick={()=>void control(j.id,false,true)}>Release vacated charging bay</button></div>}
    </article>)}
  </section>;
}
