import { useState, type FormEvent } from "react";
import { useAuth } from "../context/AuthContext";
import { useCatalog } from "../api/modules/catalog";
import { apiClient } from "../api/client";
import { useWarehouseMap } from "../context/WarehouseMapContext";

export default function WarehouseStationsPage() {
    const {role}=useAuth();const {catalog,error,reload}=useCatalog();const {warehouseLayout:layout}=useWarehouseMap();
    const [tab,setTab]=useState("stations");const [editing,setEditing]=useState<Record<string,unknown>|null>(null);
    const [message,setMessage]=useState("");const [busy,setBusy]=useState(false);
    const [warehouse,setWarehouse]=useState(0);
    const [berthStation,setBerthStation]=useState(0);const [berthRobot,setBerthRobot]=useState(0);
    const [reason,setReason]=useState("");const [clear,setClear]=useState(false);
    const admin=role==="admin";const manager=admin||role==="maintenance";
    const field="rounded border p-2 w-full bg-[var(--surface-3)] text-[var(--text-primary)]";
    const markers=layout.flat().filter(c=>c.type==="qr"&&c.arucoId!==undefined&&!c.isIntersection&&c.markerYawDeg!=null&&c.markerSizeM!=null);
    const selectedWarehouse=warehouse||catalog?.navigationWarehouseId||0;
    const rows=tab==="warehouses"?catalog?.warehouses:tab==="zones"?catalog?.zones.filter(z=>z.warehouseId===selectedWarehouse):catalog?.stations.filter(s=>s.warehouseId===selectedWarehouse);
    const idKey=tab==="warehouses"?"warehouseId":tab==="zones"?"zoneId":"stationId";
    async function save(event:FormEvent<HTMLFormElement>) {
        event.preventDefault();if(!admin||!editing)return;setBusy(true);setMessage("");
        const values=Object.fromEntries(new FormData(event.currentTarget));const body:Record<string,unknown>={...values,revision:editing.revision??0,isActive:values.isActive==="on"};
        for(const key of ["warehouseId","zoneId","speedLimit","coordX","coordY","coordZ","yawAngle","capacity"]) if(key in body)body[key]=Number(body[key]);
        if(tab==="stations")body.arucoId=values.arucoId===""?null:Number(values.arucoId);
        try {const id=editing[idKey];await apiClient.request({url:`/api/v1/warehouse-catalog/${tab}${id?`/${id}`:""}`,method:id?"PUT":"POST",data:body});setEditing(null);await reload();setMessage("Saved to database. Webots physical marker geometry was not changed.");}
        catch(e){setMessage((e as {message?:string}).message||"Save failed");}finally{setBusy(false);}
    }
    async function berth(release:boolean) {
        setBusy(true);setMessage("");try{await apiClient.post(`/api/v1/warehouse-catalog/stations/${berthStation}/berth`,{robotId:berthRobot,release,physicalAreaConfirmedClear:clear,reason});await reload();setMessage(release?"Berth released.":"Berth reserved. This does not command robot docking.");setClear(false);}
        catch(e){setMessage((e as {message?:string}).message||"Berth operation failed");}finally{setBusy(false);}
    }
    const input=(label:string,name:string,type="text",fallback:unknown="")=><label className="block text-sm" key={name}>{label}<input className={field} name={name} type={type} defaultValue={String(editing?.[name]??fallback)} required={!['address','qrCodePayload'].includes(name)} step={type==="number"?"any":undefined} /></label>;
    return <main className="p-6 overflow-y-auto text-[var(--text-primary)] space-y-4">
        <h1 className="text-xl font-bold">Warehouse &amp; Station Management</h1>
        <p className="text-sm">Station mappings link database identities to commissioned QR checkpoints. Logical edits never move Webots markers. Coordinates are local metric coordinates, separate from map grid row/column.</p>
        {error&&<p role="alert" className="text-red-500">{error}</p>}{message&&<p role="status" className="border rounded p-3">{message}</p>}
        <div className="flex gap-3 flex-wrap">{["warehouses","zones","stations"].map(t=><button className="border rounded px-3 py-2" key={t} aria-pressed={tab===t} onClick={()=>{setTab(t);setEditing(null);setMessage("");}}>{t}</button>)}
            <label>Warehouse<select className={field} value={selectedWarehouse} onChange={e=>{setWarehouse(Number(e.target.value));setEditing(null);}}>{catalog?.warehouses.map(w=><option value={w.warehouseId} key={w.warehouseId}>{w.code} — {w.name}{!w.isActive?" (inactive)":""}</option>)}</select></label>
            {admin&&<button className="border rounded px-3 py-2" onClick={()=>setEditing({})}>Add {tab.slice(0,-1)}</button>}<button className="border rounded px-3 py-2" onClick={()=>void reload()}>Reload catalog</button>
        </div>
        <div className="overflow-x-auto"><table className="w-full text-sm"><thead><tr><th>Code / Name</th><th>Type / Parent</th><th>Checkpoint / Coordinates</th><th>State / Capacity</th>{admin&&<th>Manage</th>}</tr></thead><tbody>
            {rows?.map(item=>{const r=item as unknown as Record<string,unknown>;return <tr className="border-t" key={Number(r[idKey])}><td className="p-3">{String(r.code)} — {String(r.name)}</td><td>{String(r.type??"WAREHOUSE")} · {String(r.zoneId??r.warehouseId)}</td><td>{tab==="stations"?<>{r.arucoId==null?"Unmapped":`QR${r.arucoId}`} · ({String(r.coordX)}, {String(r.coordY)})</>:"—"}</td><td>{r.isActive?"Active":"Inactive"}{tab==="stations"&&<> · {r.usable?"Ready":"Not ready"} · {r.isOccupied?"Occupied/reserved":"Free"} / {String(r.capacity)}<br/>{(r.occupantRobotCodes as string[]).join(", ")}</>}</td>{admin&&<td><button className="border rounded p-2" onClick={()=>setEditing({...r})}>Edit {String(r.code)}</button></td>}</tr>;})}
        </tbody></table></div>
        {editing&&admin&&<form key={`${tab}-${editing[idKey]??"new"}`} onSubmit={save} aria-label="Catalog editor" className="border rounded-xl p-4 grid sm:grid-cols-2 gap-3">
            <h2 className="sm:col-span-2 font-semibold">{editing[idKey]?"Edit":"Create"} {tab.slice(0,-1)} — saved history is retained; deactivate instead of deleting</h2>
            {input("Code","code")}{input("Display name","name")}
            {tab==="warehouses"?input("Address","address"):tab==="zones"?<>
                <label>Warehouse<select className={field} name="warehouseId" defaultValue={String(editing.warehouseId??selectedWarehouse)}>{catalog?.warehouses.map(w=><option value={w.warehouseId} key={w.warehouseId}>{w.code}</option>)}</select></label>
                <label>Zone type<select className={field} name="type" defaultValue={String(editing.type??"TRANSIT")}>{["TRANSIT","STORAGE","DISPATCH","PICKUP","DELIVERY","WAITING","CHARGING"].map(t=><option key={t}>{t}</option>)}</select></label>{input("Speed limit","speedLimit","number",.18)}
            </>:<>
                <label>Zone<select className={field} name="zoneId" required defaultValue={String(editing.zoneId??"")}><option value="">Select zone</option>{catalog?.zones.filter(z=>z.warehouseId===selectedWarehouse).map(z=><option key={z.zoneId} value={z.zoneId}>{z.code} — {z.name}</option>)}</select></label>
                <label>Station type<select className={field} name="type" defaultValue={String(editing.type??"PICKUP")}>{["PICKUP","DELIVERY","WAITING","CHARGING"].map(t=><option key={t}>{t}</option>)}</select></label>
                {input("X (meters)","coordX","number",0)}{input("Y (meters)","coordY","number",0)}{input("Z (meters)","coordZ","number",0)}{input("Yaw (degrees)","yawAngle","number",0)}
                {input("Station verification QR payload","qrCodePayload")}
                <label>ArUco checkpoint<select className={field} name="arucoId" defaultValue={String(editing.arucoId??"")}><option value="">Unmapped (inactive only)</option>{markers.map(m=>{const owner=catalog?.stations.find(s=>s.warehouseId===selectedWarehouse&&s.arucoId===m.arucoId);return <option key={m.arucoId} value={m.arucoId} disabled={!!owner&&owner.stationId!==editing.stationId}>QR{m.arucoId} — {m.label}{owner?` · ${owner.code}`:""}</option>;})}</select></label>
                <label>Capacity<input className={field} name="capacity" type="number" value={1} readOnly/></label>
                <p className="text-sm">One type and one physical berth per station. WAITING and CHARGING cannot be selected as pickup/delivery.</p>
            </>}
            <label><input type="checkbox" name="isActive" defaultChecked={editing.isActive!==false}/> Active</label>
            <div className="flex gap-2"><button className="rounded bg-cyan-700 text-white px-4 py-2" disabled={busy}>Save {tab.slice(0,-1)}</button><button type="button" className="border rounded p-2" onClick={()=>setEditing(null)}>Close editor</button></div>
        </form>}
        <section className="border rounded-xl p-4 space-y-3"><h2 className="font-bold">Waiting / charging capacity</h2><p className="text-sm">{catalog?.capacityPolicy}</p>
            {manager&&<><label>Berth station<select className={field} value={berthStation} onChange={e=>setBerthStation(Number(e.target.value))}><option value={0}>Select station</option>{catalog?.stations.filter(s=>s.warehouseId===selectedWarehouse&&['WAITING','CHARGING'].includes(s.type)).map(s=><option key={s.stationId} value={s.stationId}>{s.code} · QR{s.arucoId} · {s.isOccupied?'Occupied/reserved':'Free'}</option>)}</select></label>
            <label>Robot<select className={field} value={berthRobot} onChange={e=>setBerthRobot(Number(e.target.value))}><option value={0}>Select robot</option>{catalog?.robots.filter(r=>r.warehouseId===selectedWarehouse).map(r=><option value={r.robotId} key={r.robotId}>{r.robotCode}</option>)}</select></label>
            <label>Reason<input className={field} value={reason} onChange={e=>setReason(e.target.value)} maxLength={400}/></label>
            <label className="block"><input type="checkbox" checked={clear} onChange={e=>setClear(e.target.checked)}/> I confirmed the physical area is clear before releasing this reservation.</label>
            <button className="border rounded p-2 mr-2" disabled={busy||!berthStation||!berthRobot||!reason.trim()} onClick={()=>void berth(false)}>Reserve berth</button><button className="border rounded p-2" disabled={busy||!berthStation||!berthRobot||!reason.trim()||!clear} onClick={()=>void berth(true)}>Release berth</button></>}
        </section>
    </main>;
}
