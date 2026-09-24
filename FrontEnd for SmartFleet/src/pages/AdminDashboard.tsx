import { useState } from "react";
import { ROBOTS as SEED_ROBOTS, Robot } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn } from "../components/SharedUI";

export function AdminDashboard() {
    const [robots, setRobots] = useState<Robot[]>(SEED_ROBOTS);
    const [search, setSearch] = useState("");
    const [toast, setToast] = useState<string | null>(null);

    const filteredRobots = robots.filter(r => !search || r.id.toLowerCase().includes(search.toLowerCase()) || r.model.toLowerCase().includes(search.toLowerCase()));

    const toggleMaintenance = (id: string) => {
        setRobots(prev => prev.map(r => r.id === id ? { ...r, maintenance: !r.maintenance } : r));
        setToast(`Maintenance status toggled for ${id}`);
        setTimeout(() => setToast(null), 3000);
    };

    return (
        <div className="flex flex-col h-full" style={{ background: "var(--background)" }}>
            <PageHeader title="Admin CRUD Management" sub="Robot asset registry · Warehouse map node configuration">
                <PrimaryBtn onClick={() => { setToast("Add Robot modal would open here."); setTimeout(() => setToast(null), 3000); }}>
                    <Icon d={IC.plus || IC.check} size={15} /> Add New Robot
                </PrimaryBtn>
            </PageHeader>

            <div className="flex items-center gap-3 px-6 py-3 shrink-0" style={{ borderBottom: "1px solid #151d35" }}>
                <div className="relative">
                    <span className="absolute left-3 top-1/2 -translate-y-1/2" style={{ color: "var(--text-faint)" }}><Icon d={IC.search} size={14} /></span>
                    <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search robots..." className="pl-9 pr-4 py-2 rounded-xl text-[13px] text-foreground focus:outline-none" style={{ background: "var(--surface-2)", border: "1px solid #151d35", width: 280 }} />
                </div>
                <span className="font-mono text-[11px]" style={{ color: "var(--text-faint)" }}>{filteredRobots.length} robots registered</span>
            </div>

            <div className="flex-1 overflow-hidden m-6 rounded-2xl" style={{ background: "var(--surface-2)", border: "1px solid #151d35" }}>
                <div className="overflow-x-auto h-full">
                    <table className="w-full border-collapse min-w-[800px]">
                        <thead>
                            <tr style={{ borderBottom: "1px solid #151d35", background: "var(--surface-1)" }}>
                                {["Robot ID", "Model Type", "Zone", "Battery", "MQTT", "Maintenance", "Actions"].map(h => (
                                    <th key={h} className="px-4 py-3 text-left font-mono text-[10px] font-semibold tracking-wider" style={{ color: "var(--text-dimmest)" }}>{h}</th>
                                ))}
                            </tr>
                        </thead>
                        <tbody>
                            {filteredRobots.map(r => (
                                <tr key={r.id} style={{ borderBottom: "1px solid #0d1634" }}>
                                    <td className="px-4 py-3 font-mono text-[12px] font-bold" style={{ color: "#22d3ee" }}>{r.id}</td>
                                    <td className="px-4 py-3 text-[12px] text-foreground">{r.model}</td>
                                    <td className="px-4 py-3 text-[12px]" style={{ color: "#6b7fa3" }}>{r.zone}</td>
                                    <td className="px-4 py-3 font-mono text-[12px]" style={{ color: r.battery <= 20 ? "#ef4444" : "#22c55e" }}>{r.battery}%</td>
                                    <td className="px-4 py-3">
                                        <span className="font-mono text-[10px] font-semibold px-2 py-0.5 rounded" style={{ background: r.mqtt === "ONLINE" ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)", color: r.mqtt === "ONLINE" ? "#22c55e" : "#ef4444", border: `1px solid ${r.mqtt === "ONLINE" ? "rgba(34,197,94,0.2)" : "rgba(239,68,68,0.2)"}` }}>{r.mqtt}</span>
                                    </td>
                                    <td className="px-4 py-3">
                                        <button onClick={() => toggleMaintenance(r.id)} className="flex items-center gap-2 px-2 py-1 rounded-lg transition-colors" style={{ background: r.maintenance ? "rgba(245,158,11,0.12)" : "rgba(34,197,94,0.08)", border: `1px solid ${r.maintenance ? "rgba(245,158,11,0.3)" : "rgba(34,197,94,0.2)"}` }}>
                                            <div className="w-6 h-3 rounded-full relative transition-colors" style={{ background: r.maintenance ? "#f59e0b" : "#1e2d4a" }}>
                                                <span className="absolute top-0.5 left-0.5 w-2 h-2 rounded-full bg-white transition-transform" style={{ transform: r.maintenance ? "translateX(12px)" : "translateX(0)" }} />
                                            </div>
                                            <span className="font-mono text-[9px] font-bold" style={{ color: r.maintenance ? "#fbbf24" : "#22c55e" }}>{r.maintenance ? "ON" : "OFF"}</span>
                                        </button>
                                    </td>
                                    <td className="px-4 py-3">
                                        <button className="text-[11px] font-semibold px-2 py-1 rounded" style={{ color: "#818cf8", background: "rgba(99,102,241,0.1)" }}>Edit</button>
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
            </div>

            {toast && (
                <div className="fixed bottom-6 right-6 p-3 rounded-lg text-[12px] font-mono flex items-center gap-2 shadow-xl" style={{ background: "var(--surface-2)", border: "1px solid rgba(34,197,94,0.3)", color: "#4ade80", zIndex: 100 }}>
                    <Icon d={IC.check} size={14} /> {toast}
                </div>
            )}
        </div>
    );
}