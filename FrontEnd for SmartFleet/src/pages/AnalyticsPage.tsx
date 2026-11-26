import {useState,type FormEvent} from "react";
import {apiClient} from "../api/client";
import {metric,useAnalytics,vietnamDay} from "../api/modules/analytics";

export default function AnalyticsPage() {
    const today=vietnamDay();const week=new Date(today+'T00:00:00Z');week.setUTCDate(week.getUTCDate()-6);
    const [from,setFrom]=useState(week.toISOString().slice(0,10)),[to,setTo]=useState(today),[warehouse,setWarehouse]=useState(''),[robot,setRobot]=useState('');
    const [query,setQuery]=useState(`from=${from}&to=${to}`),[message,setMessage]=useState(''),[exporting,setExporting]=useState(false);
    const {report,error}=useAnalytics(query);const field="border rounded-lg p-2 bg-[var(--surface-3)] text-[var(--text-primary)]";
    function filter(e:FormEvent) {e.preventDefault();if(from>to||to>today||(Date.parse(to)-Date.parse(from))/86400000>365){setMessage('Choose an ordered range of at most 366 days ending no later than today (UTC+07).');return;}
        setMessage('');setQuery(new URLSearchParams({from,to,...(warehouse?{warehouseId:warehouse}:{}),...(robot?{robotId:robot}:{})}).toString());}
    async function download(format:'xlsx'|'pdf') {
        setExporting(true);setMessage('');try{const {data}=await apiClient.get<Blob>(`/api/v1/analytics/export/${format}?${query}`,{responseType:'blob'});const url=URL.createObjectURL(data),link=document.createElement('a');link.href=url;
            const applied=new URLSearchParams(query);link.download=`smartfleet-${applied.get('from')}-${applied.get('to')}.${format}`;link.click();window.setTimeout(()=>URL.revokeObjectURL(url),1000);
        }catch(e){setMessage((e as Error).message);}finally{setExporting(false);}
    }
    const cards=report?[
        ['Registered robots',metric(report.fleet.registered)],['Online now',metric(report.fleet.online)],['Offline now (active registrations)',metric(report.fleet.offline)],
        ['Tasks created',metric(report.tasks.created)],['Completed (creation cohort)',metric(report.tasks.completed)],['Failed (creation cohort)',metric(report.tasks.failed)],
        ['Cancelled (creation cohort)',metric(report.tasks.cancelled)],['Success rate',metric(report.tasks.successRatePercent,'%')],['Completed in period',metric(report.tasks.completedInPeriod)],
        ['Create → assignment',metric(report.tasks.averageCreateToAssignmentSeconds,' s')],['Create → completion',metric(report.tasks.averageCompletionSeconds,' s')],['Assignment utilization',metric(report.assignmentUtilizationPercent,'%')],
        ['Missions / completed / failed',`${report.missions.created} / ${report.missions.completed} / ${report.missions.failed}`],['Mission execution duration',metric(report.missions.averageExecutionSeconds,' s')],['Failed missions with active hold',metric(report.missions.failedWithActiveHold)],
        ['Maintenance created / completed',`${report.maintenance.created} / ${report.maintenance.completedInPeriod}`],['Maintenance open now',metric(report.maintenance.openNow)],['Incidents created / open now',`${report.maintenance.incidentsCreated} / ${report.maintenance.unresolvedIncidentsNow}`]
    ]:[];
    const maximum=report?Math.max(1,...report.daily.flatMap(d=>[d.createdTasks,d.completedTasks])):1;
    return <main className="sf-data-page h-full overflow-y-auto p-6 space-y-5 text-[var(--text-primary)]">
        <h1 className="text-2xl font-bold">Dashboard analytics &amp; reporting</h1><p>Database statistics refresh every 5 seconds while visible. Dates use UTC+07; fleet and open counts describe the current state. Registered totals include inactive devices; online/offline counts cover active registrations with fresh presence checks.</p>
        <form onSubmit={filter} aria-label="Analytics filters" className="flex flex-wrap items-end gap-3">
            <label>From<input className={field+" block"} type="date" required min="1970-01-01" max={today} value={from} onChange={e=>setFrom(e.target.value)}/></label>
            <label>To<input className={field+" block"} type="date" required min="1970-01-01" max={today} value={to} onChange={e=>setTo(e.target.value)}/></label>
            <label>Warehouse<select aria-label="Warehouse" className={field+" block"} value={warehouse} onChange={e=>{setWarehouse(e.target.value);setRobot('');}}><option value="">All warehouses</option>{report?.warehouses.map(w=><option key={w.warehouseId} value={w.warehouseId}>{w.code} — {w.name}</option>)}</select></label>
            <label>Robot<select aria-label="Robot" className={field+" block"} value={robot} onChange={e=>setRobot(e.target.value)}><option value="">All robots</option>{report?.robots.filter(r=>!warehouse||r.warehouseId===Number(warehouse)).map(r=><option key={r.robotId} value={r.robotId}>{r.robotCode}</option>)}</select></label>
            <button className={field}>Apply filters</button>
            <button className={field} type="button" disabled={!report||exporting} onClick={()=>void download('xlsx')}>Export Excel</button><button className={field} type="button" disabled={!report||exporting} onClick={()=>void download('pdf')}>Export PDF</button>
        </form>
        {(error||message)&&<p role="alert" className="text-red-500 border rounded p-3">{error||message}{error&&report?' — showing the last successful snapshot.':''}</p>}
        {!report&&!error&&<p role="status">Loading analytics…</p>}
        {report&&<>
            <p className="text-sm">Applied period: {report.window.from} to {report.window.to} · Snapshot: {new Date(report.window.generatedAt).toLocaleString()} · Completion duration samples: {report.tasks.completionDurationSamples} · Execution samples: {report.missions.executionDurationSamples}</p>
            <section aria-label="Analytics KPIs" className="space-y-5">
                <div className="grid sm:grid-cols-2 xl:grid-cols-4 gap-3">{[3,4,5,7].map(i=><article key={cards[i][0]} className="border rounded-xl p-5" style={{background:'var(--surface-1)',borderTop:'3px solid var(--primary)'}}><h2>{cards[i][0]}</h2><p className="font-bold mt-2">{cards[i][1]}</p></article>)}</div>
                <div className="grid lg:grid-cols-2 gap-4">{[
                    {title:'Fleet availability',indices:[0,1,2]},
                    {title:'Task performance',indices:[8,9,10,11]},
                    {title:'Mission execution',indices:[12,13,14]},
                    {title:'Exceptions & maintenance',indices:[6,15,16,17]},
                ].map(group=><article key={group.title} className="border rounded-xl p-5" style={{background:'var(--surface-1)'}}><h3 className="font-semibold mb-3">{group.title}</h3><dl className="space-y-3">{group.indices.map(i=><div key={cards[i][0]} className="flex items-center justify-between gap-4 text-xs"><dt className="sf-muted">{cards[i][0]}</dt><dd className="font-semibold shrink-0">{cards[i][1]}</dd></div>)}</dl></article>)}</div>
            </section>
            <section aria-label="Task status totals" className="border rounded-xl p-4"><h2 className="font-bold mb-3">Tasks by current status (created in period)</h2><div className="flex gap-3 flex-wrap">{Object.entries(report.tasks.statusCounts??{}).map(([status,count])=><p className="border rounded px-3 py-2" key={status}>{status}: <strong>{count}</strong></p>)}</div></section>
            <section className="border rounded-xl p-4"><h2 className="font-bold mb-3">Daily task activity</h2><p className="text-sm mb-3">Cyan: created · Green: completed, including tasks created before this period.</p><div className="max-h-80 overflow-auto space-y-2">{report.daily.map(d=><div className="flex gap-3 items-center text-sm" key={d.date}><span className="w-28 shrink-0">{d.date}</span><div className="flex-1 space-y-1" aria-hidden="true"><div className="h-2 bg-cyan-600 rounded" style={{width:`${100*d.createdTasks/maximum}%`}}/><div className="h-2 bg-emerald-600 rounded" style={{width:`${100*d.completedTasks/maximum}%`}}/></div><span className="w-40">{d.createdTasks} created · {d.completedTasks} completed</span></div>)}</div></section>
            <section className="border rounded-xl p-4 overflow-x-auto"><h2 className="font-bold mb-3">Robot assignment utilization</h2><table className="w-full text-sm text-left"><thead><tr><th>Robot</th><th>Registration</th><th>Assignments overlapping period</th><th>Completed missions in period</th><th>Held time (minutes)</th><th>Utilization</th></tr></thead><tbody>{report.robotUtilization.map(r=><tr className="border-t" key={r.robotId}><td className="py-3">{r.robotCode}</td><td>{r.isActive?'Active':'Inactive'}</td><td>{r.assignments}</td><td>{r.completedMissions}</td><td>{metric(r.assignedSeconds/60)}</td><td>{metric(r.utilizationPercent,'%')}</td></tr>)}</tbody></table>{report.robotUtilization.length===0&&<p>No robot data in this scope.</p>}</section>
            <details className="border rounded p-4"><summary className="cursor-pointer font-bold">How these metrics are calculated</summary><div className="mt-3 space-y-2 text-sm"><p>Task/mission status counts use the creation cohort and current statuses. Success rate = completed ÷ (completed + failed); cancelled tasks are excluded. No terminal samples means N/A.</p><p>Completion duration uses tasks completed in the selected period. Mission execution duration uses completed missions in the creation cohort with valid start/end timestamps. Missing timestamps are excluded.</p><p>Utilization combines overlapping assignment intervals once, divided by time since registration within the period, clipped at now. It includes held failed loads and inactive robots with history; it is not measured motor motion or device uptime.</p><p>Robot filtering includes tasks ever assigned to that robot, without counting a retried task twice. Fleet availability is a basic state check; localization, route, scene and payload checks still run when assigning a specific task.</p></div></details>
        </>}
    </main>;
}
