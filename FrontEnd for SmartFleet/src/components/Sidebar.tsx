import { Icon, IC } from "./Icons";
import { useNavigate, useLocation } from "react-router-dom";
import { useTheme } from "../context/ThemeContext";

export function Sidebar({ role, onLogout }: { role: "operator" | "admin", onLogout: () => void }) {
    const navigate = useNavigate();
    const location = useLocation();
    const { theme, toggleTheme } = useTheme();

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
        <aside
            className="w-[220px] h-full flex flex-col shrink-0"
            style={{
                background: "var(--surface-1)",
                borderRight: "1px solid var(--border-subtle)",
                transition: "background 0.2s ease, border-color 0.2s ease",
            }}
        >
            {/* Brand */}
            <div className="px-5 pt-5 pb-4" style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                <div className="flex items-center gap-3">
                    <div className="w-9 h-9 rounded-xl flex items-center justify-center relative overflow-hidden"
                        style={{ background: "linear-gradient(135deg,#0ea5e9 0%,#6366f1 100%)" }}>
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round">
                            <rect x="3" y="11" width="18" height="10" rx="2" />
                            <circle cx="12" cy="5" r="2" /><line x1="12" y1="7" x2="12" y2="11" />
                            <line x1="8" y1="16" x2="8" y2="16" strokeWidth="3.5" />
                            <line x1="16" y1="16" x2="16" y2="16" strokeWidth="3.5" />
                        </svg>
                    </div>
                    <div>
                        <div className="font-bold text-[14px] leading-tight tracking-tight" style={{ color: "var(--text-primary)" }}>
                            SmartFleet
                        </div>
                        <div className="text-[10px] font-mono" style={{ color: "#22d3ee" }}>WMS · v2.4.1</div>
                    </div>
                </div>
            </div>

            {/* Nav */}
            <nav className="flex-1 py-3 px-3 overflow-y-auto space-y-0.5">
                <div className="px-2 pb-2 pt-1">
                    <span className="font-mono text-[9px] font-semibold tracking-widest" style={{ color: "var(--text-dimmest)" }}>
                        {role.toUpperCase()} NAVIGATION
                    </span>
                </div>
                {navItems.map(({ id, label, path, icon }) => {
                    const active_ = location.pathname === path;
                    return (
                        <button key={id} onClick={() => navigate(path)}
                            className="w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-[13px] font-medium transition-all duration-150 text-left relative group"
                            style={{
                                background: active_ ? "rgba(6,182,212,0.1)" : "transparent",
                                color: active_ ? "#22d3ee" : "var(--text-faint)",
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

            {/* Broker widget */}
            <div className="mx-3 mb-3 rounded-xl p-3"
                style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}>
                <div className="flex items-center justify-between mb-2.5">
                    <span className="text-[10px] font-semibold font-mono" style={{ color: "var(--text-faint)" }}>BROKER STATUS</span>
                    <span className="flex items-center gap-1 text-[10px] font-mono" style={{ color: "#22c55e" }}>
                        <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: "#22c55e" }} />LIVE
                    </span>
                </div>
                {[
                    { k: "Robots online", v: "5 / 8", c: "#22d3ee" },
                    { k: "Msgs/sec", v: "142", c: "#a78bfa" },
                    { k: "Avg latency", v: "14 ms", c: "#34d399" },
                    { k: "Errors today", v: "2", c: "#f87171" },
                ].map(({ k, v, c }) => (
                    <div key={k} className="flex items-center justify-between py-0.5">
                        <span className="text-[10px]" style={{ color: "var(--text-faint)" }}>{k}</span>
                        <span className="font-mono text-[11px] font-semibold" style={{ color: c }}>{v}</span>
                    </div>
                ))}
            </div>

            {/* User */}
            <div className="px-4 py-3" style={{ borderTop: "1px solid var(--border-subtle)" }}>
                <div className="flex items-center gap-2.5">
                    <div className="w-7 h-7 rounded-full flex items-center justify-center text-[11px] font-bold text-foreground shrink-0"
                        style={{ background: "linear-gradient(135deg,#6366f1,#8b5cf6)" }}>DN</div>
                    <div className="min-w-0 flex-1">
                        <div className="text-[12px] font-semibold truncate" style={{ color: "var(--text-primary)" }}>Duc Nguyen</div>
                        <div className="font-mono text-[9px]" style={{ color: "var(--text-faint)" }}>
                            {role === "admin" ? "Administrator" : "Fleet Operator"}
                        </div>
                    </div>
                </div>

                {/* ⭐ THEME TOGGLE BUTTON ⭐ */}
                <button
                    onClick={toggleTheme}
                    className="mt-3 w-full py-2 rounded-lg text-[11px] font-semibold transition-all flex items-center justify-center gap-2"
                    style={{
                        background: "var(--surface-3)",
                        color: "var(--text-secondary)",
                        border: "1px solid var(--border-medium)",
                    }}
                    onMouseEnter={(e) => {
                        e.currentTarget.style.background = "var(--surface-hover)";
                        e.currentTarget.style.borderColor = "var(--border-strong)";
                    }}
                    onMouseLeave={(e) => {
                        e.currentTarget.style.background = "var(--surface-3)";
                        e.currentTarget.style.borderColor = "var(--border-medium)";
                    }}
                >
                    {theme === "dark" ? (
                        <>
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <circle cx="12" cy="12" r="4" />
                                <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41" />
                            </svg>
                            Switch to Light
                        </>
                    ) : (
                        <>
                            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                                <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
                            </svg>
                            Switch to Dark
                        </>
                    )}
                </button>

                {/* Sign Out */}
                <button
                    onClick={onLogout}
                    className="mt-2 w-full py-1.5 rounded-lg text-[11px] font-semibold transition-colors"
                    style={{
                        background: "rgba(239,68,68,0.1)",
                        color: "#f87171",
                        border: "1px solid rgba(239,68,68,0.2)",
                    }}
                >
                    Sign Out
                </button>
            </div>
        </aside>
    );
}