import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { getRobots, type RobotInfo } from "../api/modules/robots";
import LiveFleetMap from "../components/LiveFleetMap";
import { PageHeader } from "../components/SharedUI";
import OperationalSummary from "../components/OperationalSummary";

export function AdminDashboard() {
    const [robots, setRobots] = useState<RobotInfo[]>([]);
    const [error, setError] = useState("");
    useEffect(() => {
        let disposed = false;
        const refresh = async () => {
            try { const data = await getRobots(); if (!disposed) { setRobots(data); setError(""); } }
            catch (e) { if (!disposed) setError((e as Error).message); }
        };
        void refresh();
        const timer = window.setInterval(() => void refresh(), 5000);
        return () => { disposed = true; window.clearInterval(timer); };
    }, []);
    return <div className="h-full overflow-auto flex flex-col">
        <PageHeader title="Admin Dashboard" sub="Registered fleet and saved warehouse map">
            <Link to="/dashboard/robots" className="rounded-lg border px-3 py-2 text-sm">Fleet status &amp; maintenance</Link>
        </PageHeader>
        {error && <p role="alert" className="p-4 text-red-400">{error}</p>}
        <OperationalSummary />
        <p className="px-6 py-3 text-sm">{robots.length} registered robots · {robots.filter(r => r.connectionStatus === "ONLINE").length} online</p>
        <div className="min-h-[420px] h-[520px]"><LiveFleetMap robots={robots} /></div>
        <div className="p-6 overflow-auto"><table className="w-full text-sm text-left">
            <thead><tr><th>Robot</th><th>Model</th><th>Connection</th><th>Operation</th><th>Battery</th></tr></thead>
            <tbody>{robots.map(r => <tr key={r.robotId} className="border-t"><td className="py-3">{r.robotCode}</td><td>{r.model}</td><td>{r.connectionStatus}</td><td>{r.operationalStatus}</td><td>{r.batteryPercent == null ? "Unknown" : `${r.batteryPercent}%`}</td></tr>)}</tbody>
        </table></div>
    </div>;
}
