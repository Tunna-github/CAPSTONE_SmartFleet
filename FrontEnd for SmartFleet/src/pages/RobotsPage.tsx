import { useData } from "../context/DataContext";
import { Icon, IC } from "../components/Icons";

export function RobotsPage() {
    const { robots, toggleMaintenance } = useData();

    return (
        <div className="flex flex-col h-full p-6 overflow-y-auto" style={{ background: "#070d1e" }}>
            <div className="mb-6 flex items-center justify-between">
                <div>
                    <h1 className="text-[18px] font-bold text-white">Fleet Management</h1>
                    <p className="text-[12px] font-mono mt-1" style={{ color: "#4a5a80" }}>Monitor and control all autonomous mobile robots.</p>
                </div>
                <div className="flex gap-4 text-[11px] font-mono">
                    <span style={{ color: "#22c55e" }}>● {robots.filter(r => r.mqtt === "ONLINE").length} Online</span>
                    <span style={{ color: "#ef4444" }}>● {robots.filter(r => r.mqtt === "OFFLINE").length} Offline</span>
                    <span style={{ color: "#f59e0b" }}>● {robots.filter(r => r.maintenance).length} Maintenance</span>
                </div>
            </div>

            <div className="grid gap-4" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(300px, 1fr))" }}>
                {robots.map(rb => {
                    const online = rb.mqtt === "ONLINE";
                    const batColor = rb.battery <= 20 ? "#ef4444" : rb.battery <= 40 ? "#f59e0b" : "#22c55e";
                    return (
                        <div key={rb.id} className="rounded-2xl p-5 flex flex-col gap-4" style={{ background: "#0c1128", border: `1px solid ${online ? "#151d35" : "rgba(239,68,68,0.25)"}` }}>
                            <div className="flex items-center justify-between">
                                <div className="flex items-center gap-3">
                                    <div className="w-10 h-10 rounded-xl flex items-center justify-center" style={{ background: online ? "rgba(34,211,238,0.1)" : "rgba(239,68,68,0.1)", border: `1px solid ${online ? "rgba(34,211,238,0.2)" : "rgba(239,68,68,0.2)"}`, color: online ? "#22d3ee" : "#ef4444" }}>
                                        <Icon d={IC.robots} size={18} />
                                    </div>
                                    <div>
                                        <div className="font-mono text-[14px] font-bold text-white">{rb.id}</div>
                                        <div className="text-[11px]" style={{ color: "#4a5a80" }}>{rb.model}</div>
                                    </div>
                                </div>
                                <span className="font-mono text-[10px] px-2 py-1 rounded font-semibold"
                                    style={{ background: online ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)", color: online ? "#22c55e" : "#ef4444", border: `1px solid ${online ? "rgba(34,197,94,0.2)" : "rgba(239,68,68,0.2)"}` }}>
                                    {online ? "ONLINE" : "OFFLINE"}
                                </span>
                            </div>

                            <div className="rounded-xl px-4 py-3" style={{ background: "#07091a", border: "1px solid #151d35" }}>
                                <div className="text-[10px] font-mono mb-1" style={{ color: "#4a5a80" }}>CURRENT MISSION</div>
                                <div className="font-mono text-[12px] font-semibold" style={{ color: rb.missionId ? "#22d3ee" : "#2d3f66" }}>
                                    {rb.missionId || "— IDLE —"}
                                </div>
                                <div className="text-[10px] font-mono mt-1" style={{ color: "#2d3f66" }}>Zone: {rb.zone}</div>
                            </div>

                            <div>
                                <div className="flex justify-between mb-1.5">
                                    <span className="text-[11px] font-mono" style={{ color: "#4a5a80" }}>BATTERY</span>
                                    <span className="font-mono text-[12px] font-bold" style={{ color: batColor }}>{rb.battery}%</span>
                                </div>
                                <div className="h-2 rounded-full overflow-hidden" style={{ background: "#151d35" }}>
                                    <div className="h-full rounded-full transition-all" style={{ width: `${rb.battery}%`, background: batColor }} />
                                </div>
                            </div>

                            <div className="flex items-center justify-between pt-2" style={{ borderTop: "1px solid #151d35" }}>
                                <span className="text-[11px] font-mono" style={{ color: "#4a5a80" }}>MAINTENANCE MODE</span>
                                <button onClick={() => toggleMaintenance(rb.id)}
                                    className="flex items-center gap-2 px-3 py-1.5 rounded-lg transition-colors"
                                    style={{ background: rb.maintenance ? "rgba(245,158,11,0.12)" : "rgba(34,197,94,0.08)", border: `1px solid ${rb.maintenance ? "rgba(245,158,11,0.3)" : "rgba(34,197,94,0.2)"}` }}>
                                    <div className="w-8 h-4 rounded-full relative transition-colors" style={{ background: rb.maintenance ? "#f59e0b" : "#1e2d4a" }}>
                                        <span className="absolute top-0.5 left-0.5 w-3 h-3 rounded-full bg-white transition-transform" style={{ transform: rb.maintenance ? "translateX(16px)" : "translateX(0)" }} />
                                    </div>
                                    <span className="font-mono text-[10px] font-bold" style={{ color: rb.maintenance ? "#fbbf24" : "#22c55e" }}>{rb.maintenance ? "ON" : "OFF"}</span>
                                </button>
                            </div>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}