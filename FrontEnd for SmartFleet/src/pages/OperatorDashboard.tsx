import { useState, useEffect, useCallback } from "react";
import { useData } from "../context/DataContext";
import { getRobots, type RobotInfo } from "../api/modules/robots";
import { LogEntry } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { MetricCard } from "../components/SharedUI";
import { WarehouseGrid } from "../components/WarehouseGrid";

function HeartbeatRow({ robots }: { robots: RobotInfo[] }) {
    if (robots.length === 0) {
        return (
            <div className="text-center py-4 text-[11px]" style={{ color: "var(--text-faint)" }}>
                No robots found in database.
            </div>
        );
    }

    return (
        <div className="grid gap-1.5" style={{ gridTemplateColumns: `repeat(${Math.min(robots.length, 4)}, 1fr)` }}>
            {robots.map((rb) => {
                const online = rb.connectionStatus === "ONLINE";
                const battery = rb.batteryPercent ?? 0;
                const batColor = battery <= 15 ? "#ef4444" : battery <= 35 ? "#f59e0b" : "#22c55e";
                return (
                    <div
                        key={rb.robotId}
                        className="rounded-xl px-3 py-2.5 flex flex-col gap-1.5 relative overflow-hidden transition-colors"
                        style={{
                            background: online ? "var(--surface-2)" : "rgba(239,68,68,0.08)",
                            border: `1px solid ${online ? "var(--border-subtle)" : "rgba(239,68,68,0.25)"}`,
                        }}
                    >
                        {!online && (
                            <div
                                className="absolute inset-0 rounded-xl pointer-events-none"
                                style={{
                                    background: "repeating-linear-gradient(45deg,transparent,transparent 8px,rgba(239,68,68,0.03) 8px,rgba(239,68,68,0.03) 16px)",
                                }}
                            />
                        )}
                        <div className="flex items-center justify-between">
                            <span className="font-mono text-[11px] font-bold" style={{ color: "var(--text-primary)" }}>{rb.robotCode}</span>
                            <span className="flex items-center gap-1 text-[9px] font-mono" style={{ color: online ? "#22c55e" : "#ef4444" }}>
                                <span
                                    className="w-1.5 h-1.5 rounded-full"
                                    style={{ background: online ? "#22c55e" : "#ef4444", animation: online ? "pulse 2s infinite" : "none" }}
                                />
                                {online ? "LIVE" : "OFFLINE"}
                            </span>
                        </div>
                        <div>
                            <div className="flex items-center justify-between mb-0.5">
                                <span className="text-[9px]" style={{ color: "var(--text-faint)" }}>BAT</span>
                                <span className="font-mono text-[10px] font-semibold" style={{ color: batColor }}>
                                    {rb.batteryPercent !== null ? `${rb.batteryPercent}%` : "N/A"}
                                </span>
                            </div>
                            <div className="h-1 rounded-full overflow-hidden" style={{ background: "var(--border-subtle)" }}>
                                <div className="h-full rounded-full transition-all" style={{ width: `${battery}%`, background: batColor }} />
                            </div>
                        </div>
                        <div className="font-mono text-[9px] truncate" style={{ color: "var(--text-dimmest)" }}>
                            {rb.operationalStatus} {rb.hasActiveAssignment ? "· ASSIGNED" : ""}
                        </div>
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
                    <span className="ml-auto text-[10px] font-mono" style={{ color: "var(--text-dimmest)" }}>LIVE STREAM</span>
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
                                <span className="font-mono" style={{ color: "var(--text-faint)" }}>{log.robot}</span>
                                <span className="ml-auto font-mono text-[9px]" style={{ color: "var(--text-faint)" }}>{log.time}</span>
                            </div>
                            <p className="leading-snug pl-3.5" style={{ color: "var(--text-muted)" }}>{log.message}</p>
                        </div>
                    );
                })}
            </div>
        </div>
    );
}

export function OperatorDashboard() {
    const { tasks, logs, addLog } = useData();
    const [dbRobots, setDbRobots] = useState<RobotInfo[]>([]);
    const [clock, setClock] = useState(() => new Date().toLocaleTimeString("en-GB", { hour12: false }));
    const [tick, setTick] = useState(0);

    const loadBackendRobots = useCallback(async () => {
        try {
            const data = await getRobots();
            setDbRobots(data);
        } catch (err) {
            console.error("Failed to fetch fleet robots", err);
        }
    }, []);

    useEffect(() => {
        const t = setInterval(() => setClock(new Date().toLocaleTimeString("en-GB", { hour12: false })), 1000);
        return () => clearInterval(t);
    }, []);

    // Fetch real robots on mount and poll every 5 seconds
    useEffect(() => {
        void loadBackendRobots();
        const interval = setInterval(() => {
            void loadBackendRobots();
            setTick(t => t + 1);
        }, 5000);
        return () => clearInterval(interval);
    }, [loadBackendRobots]);

    // Simulate incoming MQTT logs
    useEffect(() => {
        const t = setInterval(() => {
            const activeRobots = dbRobots.filter(r => r.connectionStatus === "ONLINE");
            if (activeRobots.length > 0) {
                const rb = activeRobots[Math.floor(Math.random() * activeRobots.length)];
                const messages = [
                    `Position telemetry OK — Sector 4B`,
                    `Battery level ${rb.batteryPercent ?? "N/A"}% — nominal`,
                    `Heartbeat received — latency ${Math.floor(Math.random() * 20 + 5)}ms`,
                ];
                addLog({ level: "INFO", robot: rb.robotCode, message: messages[Math.floor(Math.random() * messages.length)] });
            }
        }, 6000);
        return () => clearInterval(t);
    }, [dbRobots, addLog]);

    const onlineCount = dbRobots.filter(r => r.connectionStatus === "ONLINE").length;
    const lowBatCount = dbRobots.filter(r => (r.batteryPercent ?? 100) <= 20).length;
    const activeTaskCount = tasks.filter(t => t.status === "EXECUTING" || t.status === "ASSIGNED").length;
    const pendingTaskCount = tasks.filter(t => t.status === "PENDING" || t.status === "QUEUED").length;

    const todayStr = new Date().toISOString().split('T')[0];
    const completedTodayCount = tasks.filter(t =>
        t.status === "COMPLETED" &&
        t.created && t.created.startsWith(todayStr)
    ).length;

    // Map dbRobots to match WarehouseGrid format if needed, or fallback
    const stationPoints = [
        { x: 9, y: 6 }, { x: 13, y: 6 }, { x: 17, y: 6 },
        { x: 9, y: 9 }, { x: 13, y: 9 }, { x: 17, y: 9 },
        { x: 9, y: 12 }, { x: 13, y: 12 }, { x: 17, y: 12 },
    ];
    const gridRobots = dbRobots.map((rb, idx) => {
        const from = stationPoints[idx % stationPoints.length]!;
        const to = stationPoints[(idx + 3) % stationPoints.length]!;
        return {
        id: rb.robotCode,
        x: from.x,
        y: from.y,
        tx: to.x,
        ty: to.y,
        battery: rb.batteryPercent ?? 100,
        mqtt: rb.connectionStatus as "ONLINE" | "OFFLINE",
        missionId: rb.hasActiveAssignment ? `Task` : null,
        zone: "Sector 4B",
        color: rb.connectionStatus === "ONLINE" ? "#22d3ee" : "#ef4444"
        };
    });

    return (
        <div className="flex flex-col h-full overflow-y-auto transition-colors" style={{ background: "var(--background)" }}>
            <div className="shrink-0 flex items-center justify-between px-6 py-3 transition-colors" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                <div>
                    <h1 className="text-[15px] font-bold tracking-tight" style={{ color: "var(--text-primary)" }}>Real-time Fleet Dashboard</h1>
                    <p className="text-[11px] font-mono mt-0.5" style={{ color: "var(--text-faint)" }}>
                        Warehouse Operations Center · Sector 4B · <span style={{ color: "#22d3ee" }}>{clock}</span>
                    </p>
                </div>
                <div className="flex items-center gap-3">
                    <div className="flex items-center gap-4 px-4 py-2 rounded-xl transition-colors" style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}>
                        {[{ label: "API", ok: true }, { label: "DB", ok: true }, { label: "MQTT", ok: true }, { label: "MAP", ok: true }].map(s => (
                            <div key={s.label} className="flex items-center gap-1.5">
                                <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: s.ok ? "#22c55e" : "#ef4444" }} />
                                <span className="font-mono text-[10px]" style={{ color: s.ok ? "var(--text-faint)" : "#ef4444" }}>{s.label}</span>
                            </div>
                        ))}
                    </div>
                </div>
            </div>

            <div className="shrink-0 flex gap-3 px-6 py-3" style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                <MetricCard label="Active Robots" value={`${onlineCount}/${dbRobots.length}`} sub="Live telemetry connected" trend="vs. yesterday" trendUp={true} accentColor="#22d3ee" glowColor="#06b6d4" iconEl={<Icon d={IC.robots} size={18} />} />
                <MetricCard label="Tasks in Queue" value={`${pendingTaskCount}`} sub={`${activeTaskCount} executing · ${pendingTaskCount} pending`} trend="2 high priority" trendUp={false} accentColor="#818cf8" glowColor="#6366f1" iconEl={<Icon d={IC.tasks} size={18} />} />
                <MetricCard label="Battery Low Alerts" value={`${lowBatCount}`} sub="Robots below 20% battery" trend="Critical" trendUp={false} accentColor="#f87171" glowColor="#ef4444" iconEl={<Icon d={IC.battery} size={18} />} />
                <MetricCard label="Today's Completion" value={`${completedTodayCount}`} sub="tasks completed today" trend="On target" trendUp={true} accentColor="#34d399" glowColor="#10b981" iconEl={<Icon d={IC.check} size={18} />} />
                <MetricCard label="MQTT Throughput" value="142" sub="messages / second · 14ms avg" trend="Normal" trendUp={true} accentColor="#a78bfa" glowColor="#7c3aed" iconEl={<Icon d={IC.activity} size={18} />} />
            </div>

            <div className="flex-1 flex gap-0 min-h-0">
                {/* LEFT: Map Area (70%) */}
                <div className="flex flex-col min-w-0" style={{ flex: "0 0 70%", borderRight: "1px solid var(--border-subtle)" }}>
                    <div className="shrink-0 flex items-center justify-between px-4 py-2.5 transition-colors" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                        <div className="flex items-center gap-3">
                            <span className="text-[12px] font-semibold" style={{ color: "var(--text-primary)" }}>Live Warehouse Grid — Sector 4B</span>
                            <span className="flex items-center gap-1.5 text-[10px] font-mono px-2 py-0.5 rounded-full" style={{ background: "rgba(34,211,238,0.1)", color: "#22d3ee", border: "1px solid rgba(34,211,238,0.2)" }}>
                                <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: "#22d3ee" }} />LIVE · {tick}
                            </span>
                        </div>
                        <div className="flex items-center gap-4 text-[10px] font-mono" style={{ color: "var(--text-dimmest)" }}>
                            {[{ color: "#22d3ee", label: "Robot Active" }, { color: "#ef4444", label: "Robot Offline" }, { color: "#06b6d4", label: "Station (Pick/Drop)" }, { color: "#f59e0b", label: "Charging" }, { color: "#8b5cf6", label: "Dock" }].map(({ color, label }) => (
                                <span key={label} className="flex items-center gap-1.5"><span className="w-2 h-2 rounded-sm" style={{ background: color }} />{label}</span>
                            ))}
                        </div>
                    </div>
                    <div className="flex-1 overflow-hidden relative" style={{ background: "var(--surface-3)", minHeight: "400px" }}>
                        <WarehouseGrid robots={gridRobots} />
                    </div>
                    <div className="shrink-0 px-4 py-3 transition-colors" style={{ borderTop: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                        <div className="flex items-center justify-between mb-2">
                            <span className="text-[11px] font-semibold" style={{ color: "var(--text-primary)" }}>Fleet Heartbeat Monitor (Database)</span>
                            <span className="font-mono text-[10px]" style={{ color: "var(--text-dimmest)" }}>Polling every 5s</span>
                        </div>
                        <HeartbeatRow robots={dbRobots} />
                    </div>
                </div>

                {/* RIGHT: Side Panel (30%) */}
                <div className="flex flex-col min-h-0" style={{ flex: "0 0 30%", overflowY: "auto" }}>

                    {/* MQTT Connectivity */}
                    <div className="shrink-0 px-4 py-3 transition-colors" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                        <div className="flex items-center justify-between mb-3">
                            <span className="text-[12px] font-semibold" style={{ color: "var(--text-primary)" }}>MQTT Connectivity</span>
                            <span className="font-mono text-[9px] px-2 py-0.5 rounded-full" style={{ background: "rgba(34,197,94,0.1)", color: "#22c55e", border: "1px solid rgba(34,197,94,0.2)" }}>BROKER ONLINE</span>
                        </div>
                        <div className="grid grid-cols-2 gap-2">
                            {dbRobots.map(rb => {
                                const online = rb.connectionStatus === "ONLINE";
                                return (
                                    <div key={rb.robotId} className="flex items-center justify-between px-2.5 py-1.5 rounded-lg transition-colors" style={{ background: online ? "rgba(34,211,238,0.05)" : "rgba(239,68,68,0.08)", border: `1px solid ${online ? "rgba(34,211,238,0.12)" : "rgba(239,68,68,0.2)"}` }}>
                                        <div className="flex items-center gap-1.5">
                                            <span className="w-1.5 h-1.5 rounded-full" style={{ background: online ? "#22c55e" : "#ef4444", boxShadow: online ? "0 0 4px #22c55e" : "0 0 4px #ef4444" }} />
                                            <span className="font-mono text-[10px] font-semibold" style={{ color: "var(--text-primary)" }}>{rb.robotCode}</span>
                                        </div>
                                        <div className="flex items-center gap-1">
                                            {online ? (
                                                <span className="font-mono text-[9px]" style={{ color: "#22c55e" }}>LIVE</span>
                                            ) : (
                                                <span className="font-mono text-[9px]" style={{ color: "#ef4444" }}>OFFLINE</span>
                                            )}
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                    </div>

                    {/* Active Exceptions */}
                    <div className="shrink-0 px-4 py-3" style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                        <div className="flex items-center justify-between mb-2">
                            <span className="text-[12px] font-semibold" style={{ color: "var(--text-primary)" }}>Active Exceptions</span>
                            <span className="font-mono text-[9px] px-2 py-0.5 rounded-full" style={{ background: "rgba(239,68,68,0.12)", color: "#f87171", border: "1px solid rgba(239,68,68,0.25)" }}>
                                {dbRobots.reduce((acc, r) => acc + (r.openIncidents?.length || 0), 0)} INCIDENTS
                            </span>
                        </div>
                        <div className="space-y-1.5">
                            {dbRobots.flatMap(r => r.openIncidents || []).length === 0 ? (
                                <p className="text-[11px] italic opacity-60">No unresolved robot incidents.</p>
                            ) : (
                                dbRobots.flatMap(r => (r.openIncidents || []).map(inc => ({ ...inc, robotCode: r.robotCode }))).map((inc, i) => (
                                    <div key={i} className="flex items-center gap-2 px-2.5 py-2 rounded-lg text-[11px]" style={{ background: `rgba(239,68,68,0.08)`, border: `1px solid rgba(239,68,68,0.2)` }}>
                                        <span className="shrink-0 text-red-400"><Icon d={IC.alert} size={12} /></span>
                                        <span className="font-mono font-semibold shrink-0 text-[10px] text-red-400">{inc.robotCode}</span>
                                        <span style={{ color: "var(--text-muted)" }} className="truncate">{inc.incidentType} — {inc.description}</span>
                                    </div>
                                ))
                            )}
                        </div>
                    </div>

                    {/* Event Log */}
                    <div className="flex-1 flex flex-col min-h-[300px] px-4 py-3">
                        <div className="flex items-center justify-between mb-2 shrink-0">
                            <span className="text-[12px] font-semibold" style={{ color: "var(--text-primary)" }}>Event Log</span>
                            <span className="font-mono text-[9px]" style={{ color: "var(--text-dimmest)" }}>Auto-streaming</span>
                        </div>
                        <div className="flex-1"><LogPanel logs={logs} /></div>
                    </div>
                </div>
            </div>
        </div>
    );
}
