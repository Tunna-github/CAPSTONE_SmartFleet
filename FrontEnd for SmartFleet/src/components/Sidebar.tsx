import { Icon, IC } from "./Icons";
import { useNavigate, useLocation } from "react-router-dom";

export function Sidebar({ role, onLogout }: { role: "operator" | "admin", onLogout: () => void }) {
    const navigate = useNavigate();
    const location = useLocation();

    const navItems = role === "admin"
        ? [
            { id: "admin-dashboard", label: "Admin Dashboard", path: "/admin", icon: IC.dashboard },
            { id: "robots", label: "Robot Assets", path: "/admin/robots", icon: IC.robots },
            { id: "maps", label: "Warehouse Maps", path: "/admin/maps", icon: IC.maps },
        ]
        : [
            { id: "dashboard", label: "Dashboard", path: "/dashboard", icon: IC.dashboard },
            { id: "tasks", label: "Create Task", path: "/dashboard/create-task", icon: IC.tasks },
            { id: "robots", label: "Fleet Status", path: "/dashboard/robots", icon: IC.robots },
            { id: "analytics", label: "Analytics", path: "/dashboard/analytics", icon: IC.analytics },
        ];

    return (
        <aside className="w-[220px] h-full flex flex-col shrink-0" style={{ background: "#07091a", borderRight: "1px solid #151d35" }}>
            <div className="px-5 pt-5 pb-4" style={{ borderBottom: "1px solid #151d35" }}>
                <div className="flex items-center gap-3">
                    <div className="w-9 h-9 rounded-xl flex items-center justify-center relative overflow-hidden" style={{ background: "linear-gradient(135deg,#0ea5e9 0%,#6366f1 100%)" }}>
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                            <rect x="3" y="11" width="18" height="10" rx="2" /><circle cx="12" cy="5" r="2" /><line x1="12" y1="7" x2="12" y2="11" /><line x1="8" y1="16" x2="8" y2="16" strokeWidth="3.5" /><line x1="16" y1="16" x2="16" y2="16" strokeWidth="3.5" />
                        </svg>
                    </div>
                    <div>
                        <div className="text-white font-bold text-[14px] leading-tight tracking-tight">SmartFleet</div>
                        <div className="text-[10px] font-mono" style={{ color: "#22d3ee" }}>WMS · v2.4.1</div>
                    </div>
                </div>
            </div>

            <nav className="flex-1 py-3 px-3 overflow-y-auto space-y-0.5">
                <div className="px-2 pb-2 pt-1">
                    <span className="font-mono text-[9px] font-semibold tracking-widest" style={{ color: "#2d3f66" }}>{role.toUpperCase()} NAVIGATION</span>
                </div>
                {navItems.map(({ id, label, path, icon }) => {
                    const active_ = location.pathname === path;
                    return (
                        <button key={id} onClick={() => navigate(path)}
                            className="w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-[13px] font-medium transition-all duration-150 text-left relative group"
                            style={{
                                background: active_ ? "rgba(6,182,212,0.1)" : "transparent",
                                color: active_ ? "#22d3ee" : "#4a5a80",
                                border: active_ ? "1px solid rgba(6,182,212,0.2)" : "1px solid transparent",
                            }}
                        >
                            {active_ && <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 rounded-r-full" style={{ background: "#22d3ee" }} />}
                            <span style={{ opacity: active_ ? 1 : 0.6 }}><Icon d={icon} size={16} /></span>
                            {label}
                        </button>
                    );
                })}
            </nav>

            <div className="mx-3 mb-3 rounded-xl p-3" style={{ background: "#0c1128", border: "1px solid #151d35" }}>
                <div className="flex items-center justify-between mb-2.5">
                    <span className="text-[10px] font-semibold font-mono" style={{ color: "#4a5a80" }}>BROKER STATUS</span>
                    <span className="flex items-center gap-1 text-[10px] font-mono" style={{ color: "#22c55e" }}><span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: "#22c55e" }} />LIVE</span>
                </div>
                {[{ k: "Robots online", v: "5 / 8", c: "#22d3ee" }, { k: "Msgs/sec", v: "142", c: "#a78bfa" }, { k: "Avg latency", v: "14 ms", c: "#34d399" }, { k: "Errors today", v: "2", c: "#f87171" }].map(({ k, v, c }) => (
                    <div key={k} className="flex items-center justify-between py-0.5">
                        <span className="text-[10px]" style={{ color: "#4a5a80" }}>{k}</span>
                        <span className="font-mono text-[11px] font-semibold" style={{ color: c }}>{v}</span>
                    </div>
                ))}
            </div>

            <div className="px-4 py-3" style={{ borderTop: "1px solid #151d35" }}>
                <div className="flex items-center gap-2.5">
                    <div className="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-bold text-white shrink-0" style={{ background: "linear-gradient(135deg,#6366f1,#8b5cf6)" }}>DN</div>
                    <div className="min-w-0 flex-1">
                        <div className="text-[12px] font-semibold text-white truncate">Duc Nguyen</div>
                        <div className="font-mono text-[9px]" style={{ color: "#4a5a80" }}>{role === "admin" ? "Administrator" : "Fleet Operator"}</div>
                    </div>
                </div>
                <button onClick={onLogout} className="mt-3 w-full py-1.5 rounded-lg text-[11px] font-semibold transition-colors" style={{ background: "rgba(239,68,68,0.1)", color: "#f87171", border: "1px solid rgba(239,68,68,0.2)" }}>
                    Sign Out
                </button>
            </div>
        </aside>
    );
}