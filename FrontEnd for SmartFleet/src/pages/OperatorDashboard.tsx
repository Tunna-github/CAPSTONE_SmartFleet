import { useState, useEffect, useRef } from "react";
import { useData } from "../context/DataContext";
import { INIT_ROBOTS, RobotPos, LogEntry } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { MetricCard } from "../components/SharedUI";
import { WarehouseGrid } from "../components/WarehouseGrid";

function HeartbeatRow({ robots }: { robots: RobotPos[] }) {
    return (
        <div className="grid gap-1.5" style={{ gridTemplateColumns: "repeat(4,1fr)" }}>
            {robots.map((rb) => {
                const online = rb.mqtt === "ONLINE";
                const batColor = rb.battery <= 15 ? "#ef4444" : rb.battery <= 35 ? "#f59e0b" : "#22c55e";
                return (
                    <div key={rb.id} className="rounded-xl px-3 py-2.5 flex flex-col gap-1.5 relative overflow-hidden"
                        style={{ background: online ? "#0c1128" : "#150a0a", border: `1px solid ${online ? "#151d35" : "rgba(239,68,68,0.25)"}` }}>
                        {!online && (<div className="absolute inset-0 rounded-xl pointer-events-none" style={{ background: "repeating-linear-gradient(45deg,transparent,transparent 8px,rgba(239,68,68,0.03) 8px,rgba(239,68,68,0.03) 16px)" }} />)}
                        <div className="flex items-center justify-between">
                            <span className="font-mono text-[11px] font-bold text-white">{rb.id}</span>
                            <span className="flex items-center gap-1 text-[9px] font-mono" style={{ color: online ? "#22c55e" : "#ef4444" }}>
                                <span className="w-1.5 h-1.5 rounded-full" style={{ background: online ? "#22c55e" : "#ef4444", animation: online ? "pulse 2s infinite" : "none" }} />{online ? "LIVE" : "DEAD"}
                            </span>
                        </div>
                        <div>
                            <div className="flex items-center justify-between mb-0.5">
                                <span className="text-[9px]" style={{ color: "#4a5a80" }}>BAT</span>
                                <span className="font-mono text-[10px] font-semibold" style={{ color: batColor }}>{rb.battery}%</span>
                            </div>
                            <div className="h-1 rounded-full overflow-hidden" style={{ background: "#151d35" }}>
                                <div className="h-full rounded-full transition-all" style={{ width: `${rb.battery}%`, background: batColor }} />
                            </div>
                        </div>
                        <div className="font-mono text-[9px] truncate" style={{ color: "#2d3f66" }}>{rb.missionId || "IDLE"}</div>
                    </div>
                );
            })}
        </div>
    );
}

function LogPanel({ logs }: { logs: LogEntry[] }) {
    const logColors: Record<string, { bg: string; text: string; dot: string }> = {
        ERROR: { bg: "rgba(239,68,68,0.08)", text: "#f87171", dot: "#ef4444" },
        WARN: { bg: "rgba(245,158,11,0.08)", text: "#fbbf24", dot: "#f59e0b" },
        INFO: { bg: "rgba(99,102,241,0.06)", text: "#818cf8", dot: "#6366f1" },
        SUCCESS: { bg: "rgba(34,197,94,0.08)", text: "#4ade80", dot: "#22c55e" },
    };

    return (
        <div className="flex flex-col h-full overflow-hidden">
            <div className="shrink-0 px-1 pb-2">
                <div className="flex items-center gap-1.5 flex-wrap">
                    {Object.entries(logColors).map(([lvl, c]) => (
                        <span key={lvl} className="flex items-center gap-1 text-[10px] font-mono" style={{ color: c.text }}>
                            <span className="w-1.5 h-1.5 rounded-full" style={{ background: c.dot }} />{lvl}
                        </span>
                    ))}
                    <span className="ml-auto text-[10px] font-mono" style={{ color: "#2d3f66" }}>LIVE STREAM</span>
                </div>
            </div>
            <div className="flex-1 overflow-y-auto space-y-1 pr-1">
                {logs.map((log) => {
                    const c = logColors[log.level];
                    return (
                        <div key={log.id} className="rounded-lg px-3 py-2 text-[11px] transition-all" style={{ background: c.bg, border: `1px solid ${c.dot}18` }}>
                            <div className="flex items-center gap-2 mb-0.5">
                                <span className="w-1.5 h-1.5 rounded-full shrink-0" style={{ background: c.dot }} />
                                <span className="font-mono font-semibold" style={{ color: c.text }}>{log.level}</span>
                                <span className="font-mono" style={{ color: "#4a5a80" }}>{log.robot}</span>
                                <span className="ml-auto font-mono text-[9px]" style={{ color: "#2d3f66" }}>{log.time}</span>
                            </div>
                            <p className="leading-snug pl-3.5" style={{ color: "#8899bb" }}>{log.message}</p>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

export function OperatorDashboard() {
    const { tasks, robots, logs, addLog } = useData();
    const [robotPositions, setRobotPositions] = useState<RobotPos[]>(INIT_ROBOTS);
    const [clock, setClock] = useState(() => new Date().toLocaleTimeString("en-GB", { hour12: false }));
    const [tick, setTick] = useState(0);
    const nextLogId = useRef(100);

    useEffect(() => {
        const t = setInterval(() => setClock(new Date().toLocaleTimeString("en-GB", { hour12: false })), 1000);
        return () => clearInterval(t);
    }, []);

    // Animation loop for robots
    useEffect(() => {
        const t = setInterval(() => {
            setRobotPositions(prev => prev.map(rb => {
                const robotData = robots.find(r => r.id === rb.id);
                const isOnline = robotData?.mqtt === "ONLINE";
                const missionId = robotData?.missionId;

                if (!isOnline || !missionId) return { ...rb, missionId: null, mqtt: isOnline ? "ONLINE" : "OFFLINE" };

                const dx = rb.tx - rb.x; const dy = rb.ty - rb.y;
                if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) {
                    const newTx = Math.max(1, Math.min(26, rb.tx + (Math.random() > 0.5 ? 3 : -3)));
                    const newTy = Math.max(1, Math.min(16, rb.ty + (Math.random() > 0.5 ? 2 : -2)));
                    return { ...rb, x: rb.tx, y: rb.ty, tx: newTx, ty: newTy, missionId, mqtt: "ONLINE" };
                }
                return { ...rb, x: rb.x + dx * 0.08, y: rb.y + dy * 0.08, missionId, mqtt: "ONLINE" };
            }));
            setTick(t => t + 1);
        }, 150);
        return () => clearInterval(t);
    }, [robots]);

    // Simulate incoming MQTT logs
    useEffect(() => {
        const t = setInterval(() => {
            const activeRobots = robots.filter(r => r.mqtt === "ONLINE");
            if (activeRobots.length > 0) {
                const rb = activeRobots[Math.floor(Math.random() * activeRobots.length)];
                const messages = [
                    `Position telemetry OK — ${rb.zone}`,
                    `Battery level ${rb.battery}% — nominal`,
                    `Heartbeat received — latency ${Math.floor(Math.random() * 20 + 5)}ms`,
                ];
                addLog({ level: "INFO", robot: rb.id, message: messages[Math.floor(Math.random() * messages.length)] });
            }
        }, 5000);
        return () => clearInterval(t);
    }, [robots, addLog]);

    const onlineCount = robots.filter(r => r.mqtt === "ONLINE").length;
    const lowBatCount = robots.filter(r => r.battery <= 20).length;
    const activeTaskCount = tasks.filter(t => t.status === "EXECUTING" || t.status === "ASSIGNED").length;
    const pendingTaskCount = tasks.filter(t => t.status === "PENDING" || t.status === "QUEUED").length;

    return (
        <div className="flex flex-col h-full overflow-y-auto" style={{ background: "#070d1e" }}>
            <div className="shrink-0 flex items-center justify-between px-6 py-3" style={{ borderBottom: "1px solid #151d35", background: "#07091a" }}>
                <div>
                    <h1 className="text-[15px] font-bold text-white tracking-tight">Real-time Fleet Dashboard</h1>
                    <p className="text-[11px] font-mono mt-0.5" style={{ color: "#4a5a80" }}>Warehouse Operations Center · Sector 4B · <span style={{ color: "#22d3ee" }}>{clock}</span></p>
                </div>
                <div className="flex items-center gap-3">
                    <div className="flex items-center gap-4 px-4 py-2 rounded-xl" style={{ background: "#0c1128", border: "1px solid #151d35" }}>
                        {[{ label: "API", ok: true }, { label: "DB", ok: true }, { label: "MQTT", ok: true }, { label: "MAP", ok: true }].map(s => (
                            <div key={s.label} className="flex items-center gap-1.5">
                                <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: s.ok ? "#22c55e" : "#ef4444" }} />
                                <span className="font-mono text-[10px]" style={{ color: s.ok ? "#4a5a80" : "#ef4444" }}>{s.label}</span>
                            </div>
                        ))}
                    </div>
                </div>
            </div>

            <div className="shrink-0 flex gap-3 px-6 py-3" style={{ borderBottom: "1px solid #151d35" }}>
                <MetricCard label="Active Robots" value={`${onlineCount}`} sub={`${8 - onlineCount} offline · 2 charging`} trend="vs. yesterday" trendUp={true} accentColor="#22d3ee" glowColor="#06b6d4" iconEl={<Icon d={IC.robots} size={18} />} />
                <MetricCard label="Tasks in Queue" value={`${pendingTaskCount}`} sub={`${activeTaskCount} executing · ${pendingTaskCount} pending`} trend="2 high priority" trendUp={false} accentColor="#818cf8" glowColor="#6366f1" iconEl={<Icon d={IC.tasks} size={18} />} />
                <MetricCard label="Battery Low Alerts" value={`${lowBatCount}`} sub="AMR-003 (12%) · AMR-006 (8%)" trend="Critical" trendUp={false} accentColor="#f87171" glowColor="#ef4444" iconEl={<Icon d={IC.battery} size={18} />} />
                <MetricCard label="Today's Completion" value="94.2%" sub="47 tasks done · ↑ 2.1% vs. avg" trend="On target" trendUp={true} accentColor="#34d399" glowColor="#10b981" iconEl={<Icon d={IC.check} size={18} />} />
                <MetricCard label="MQTT Throughput" value="142" sub="messages / second · 14ms avg" trend="Normal" trendUp={true} accentColor="#a78bfa" glowColor="#7c3aed" iconEl={<Icon d={IC.activity} size={18} />} />
            </div>

            <div className="flex-1 flex gap-0 min-h-0">
                {/* LEFT: Map Area (70%) */}
                <div className="flex flex-col min-w-0" style={{ flex: "0 0 70%", borderRight: "1px solid #151d35" }}>
                    <div className="shrink-0 flex items-center justify-between px-4 py-2.5" style={{ borderBottom: "1px solid #151d35", background: "#07091a" }}>
                        <div className="flex items-center gap-3">
                            <span className="text-[12px] font-semibold text-white">Live Warehouse Grid — Sector 4B</span>
                            <span className="flex items-center gap-1.5 text-[10px] font-mono px-2 py-0.5 rounded-full" style={{ background: "rgba(34,211,238,0.1)", color: "#22d3ee", border: "1px solid rgba(34,211,238,0.2)" }}>
                                <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: "#22d3ee" }} />LIVE · {tick}
                            </span>
                        </div>
                        <div className="flex items-center gap-4 text-[10px] font-mono" style={{ color: "#2d3f66" }}>
                            {[{ color: "#22d3ee", label: "Robot Active" }, { color: "#ef4444", label: "Robot Offline" }, { color: "#3b82f6", label: "Pickup" }, { color: "#22c55e", label: "Delivery" }, { color: "#f59e0b", label: "Charging" }, { color: "#8b5cf6", label: "Dock" }].map(({ color, label }) => (
                                <span key={label} className="flex items-center gap-1.5"><span className="w-2 h-2 rounded-sm" style={{ background: color }} />{label}</span>
                            ))}
                        </div>
                    </div>
                    <div className="flex-1 overflow-hidden relative" style={{ background: "#060d1f", minHeight: "400px" }}><WarehouseGrid robots={robotPositions} /></div>
                    <div className="shrink-0 px-4 py-3" style={{ borderTop: "1px solid #151d35", background: "#07091a" }}>
                        <div className="flex items-center justify-between mb-2">
                            <span className="text-[11px] font-semibold text-white">Fleet Heartbeat Monitor</span>
                            <span className="font-mono text-[10px]" style={{ color: "#2d3f66" }}>Updated every 150ms</span>
                        </div>
                        <HeartbeatRow robots={robotPositions} />
                    </div>
                </div>

                {/* RIGHT: Side Panel (30%) */}
                {/* KEY CHANGE: This is now a single scrollable column instead of fixed sections */}
                <div className="flex flex-col min-h-0" style={{ flex: "0 0 30%", overflowY: "auto" }}>

                    {/* MQTT Connectivity */}
                    <div className="shrink-0 px-4 py-3" style={{ borderBottom: "1px solid #151d35", background: "#07091a" }}>
                        <div className="flex items-center justify-between mb-3">
                            <span className="text-[12px] font-semibold text-white">MQTT Connectivity</span>
                            <span className="font-mono text-[9px] px-2 py-0.5 rounded-full" style={{ background: "rgba(34,197,94,0.1)", color: "#22c55e", border: "1px solid rgba(34,197,94,0.2)" }}>BROKER ONLINE</span>
                        </div>
                        <div className="grid grid-cols-2 gap-2">
                            {robotPositions.map(rb => {
                                const online = rb.mqtt === "ONLINE";
                                return (
                                    <div key={rb.id} className="flex items-center justify-between px-2.5 py-1.5 rounded-lg" style={{ background: online ? "rgba(34,211,238,0.05)" : "rgba(239,68,68,0.08)", border: `1px solid ${online ? "rgba(34,211,238,0.12)" : "rgba(239,68,68,0.2)"}` }}>
                                        <div className="flex items-center gap-1.5">
                                            <span className="w-1.5 h-1.5 rounded-full" style={{ background: online ? "#22c55e" : "#ef4444", boxShadow: online ? "0 0 4px #22c55e" : "0 0 4px #ef4444" }} />
                                            <span className="font-mono text-[10px] font-semibold text-white">{rb.id}</span>
                                        </div>
                                        <div className="flex items-center gap-1">
                                            {online ? (<><span className="font-mono text-[9px]" style={{ color: "#22c55e" }}>LIVE</span><span className="font-mono text-[8px]" style={{ color: "#2d3f66" }}>·{Math.floor(Math.random() * 20 + 5)}ms</span></>) : (<span className="font-mono text-[9px]" style={{ color: "#ef4444" }}>OFFLINE</span>)}
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                    </div>

                    {/* Active Exceptions */}
                    <div className="shrink-0 px-4 py-3" style={{ borderBottom: "1px solid #151d35" }}>
                        <div className="flex items-center justify-between mb-2">
                            <span className="text-[12px] font-semibold text-white">Active Exceptions</span>
                            <span className="font-mono text-[9px] px-2 py-0.5 rounded-full" style={{ background: "rgba(239,68,68,0.12)", color: "#f87171", border: "1px solid rgba(239,68,68,0.25)" }}>2 CRITICAL</span>
                        </div>
                        <div className="space-y-1.5">
                            {[{ level: "CRITICAL", robot: "AMR-003", msg: "Offline — heartbeat lost 4m 12s", color: "#ef4444" }, { level: "CRITICAL", robot: "AMR-006", msg: "Battery 8% — mission halted", color: "#ef4444" }, { level: "WARNING", robot: "AMR-003", msg: "Battery 12% — dock return req.", color: "#f59e0b" }, { level: "WARNING", robot: "AMR-008", msg: "Maintenance mode active", color: "#f59e0b" }].map((ex, i) => (
                                <div key={i} className="flex items-center gap-2 px-2.5 py-2 rounded-lg text-[11px]" style={{ background: `${ex.color}0d`, border: `1px solid ${ex.color}22` }}>
                                    <span className="shrink-0" style={{ color: ex.color }}><Icon d={IC.alert} size={12} /></span>
                                    <span className="font-mono font-semibold shrink-0 text-[10px]" style={{ color: ex.color }}>{ex.robot}</span>
                                    <span style={{ color: "#6b7fa3" }} className="truncate">{ex.msg}</span>
                                </div>
                            ))}
                        </div>
                    </div>

                    {/* Event Log — Now expands naturally within the scrollable column */}
                    <div className="flex-1 flex flex-col min-h-[300px] px-4 py-3">
                        <div className="flex items-center justify-between mb-2 shrink-0">
                            <span className="text-[12px] font-semibold text-white">Event Log</span>
                            <span className="font-mono text-[9px]" style={{ color: "#2d3f66" }}>Auto-streaming</span>
                        </div>
                        <div className="flex-1"><LogPanel logs={logs} /></div>
                    </div>
                </div>
            </div>
        </div>
    );
}