import {Link} from "react-router-dom";
import {useAnalytics,vietnamDay} from "../api/modules/analytics";

export default function OperationalSummary() {
    const today=vietnamDay();const {report,error}=useAnalytics(`from=${today}&to=${today}`);
    const metrics=[['Online / registered',report?`${report.fleet.online} / ${report.fleet.registered}`:'—'],['Queued now',report?.tasks.liveQueue??'—'],
        ['Executing now',report?.tasks.liveExecuting??'—'],['Completed today (UTC+07)',report?.tasks.completedInPeriod??'—'],['Low battery / unknown',report?`${report.fleet.lowBattery} / ${report.fleet.unknownBattery}`:'—'],['Open incidents',report?.maintenance.unresolvedIncidentsNow??'—']];
    return <section aria-label="Operational summary" className="shrink-0 px-6 py-4 space-y-2">
        {error&&<p role="alert" className="text-red-500">Analytics unavailable: {error}{report?' — showing the last successful snapshot.':''}</p>}
        <div className="grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">{metrics.map(([label,value])=><div className="border rounded-xl p-3" style={{background:'var(--surface-2)'}} key={label}><p className="text-xs">{label}</p><p className="text-2xl font-bold mt-2">{value}</p></div>)}</div>
        <p className="text-xs">{report?`Updated ${new Date(report.window.generatedAt).toLocaleTimeString()}`:'Loading database summary…'} · <Link className="underline" to="/dashboard/analytics">View analytics &amp; export reports</Link></p>
    </section>;
}
