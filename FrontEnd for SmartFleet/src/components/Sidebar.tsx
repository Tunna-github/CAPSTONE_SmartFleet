import { Icon, IC } from "./Icons";
import { useNavigate, useLocation } from "react-router-dom";
import { useTheme } from "../context/ThemeContext";
import { useAuth, type Role } from "../context/AuthContext";

export function Sidebar({
    role,
    onLogout,
    isCollapsed,
    toggleCollapse,
}: {
    role: Role;
    onLogout: () => void;
    isCollapsed: boolean;
    toggleCollapse: () => void;
}) {
    const navigate = useNavigate();
    const location = useLocation();
    const { theme, toggleTheme } = useTheme();
    const { user } = useAuth();
    const userName = user?.fullName || user?.username || "SmartFleet User";
    const userInitials = userName.trim().split(/\s+/).slice(0, 2).map((part) => part[0]?.toUpperCase() ?? "").join("") || "SF";

    const navItems =
        role === "admin"
            ? [
                { id: "admin-dashboard", label: "Admin Dashboard", path: "/admin", icon: IC.dashboard },
                { id: "users", label: "User Management", path: "/admin/users", icon: IC.users },
                { id: "robots", label: "Robot Assets", path: "/admin/robots", icon: IC.robots },
                { id: "maps", label: "Warehouse Maps", path: "/admin/maps", icon: IC.maps },
            ]
            : role === "maintenance"
                ? [
                    { id: "robots", label: "Fleet Status", path: "/dashboard/robots", icon: IC.robots },
                    { id: "map", label: "Live Map", path: "/dashboard/map", icon: IC.maps },
                    { id: "maintenance", label: "Maintenance", path: "/dashboard/maintenance", icon: IC.settings },
                ]
                : [
                    { id: "dashboard", label: "Dashboard", path: "/dashboard", icon: IC.dashboard },
                    { id: "tasks", label: "Tasks", path: "/dashboard/tasks", icon: IC.tasks },
                    { id: "robots", label: "Fleet Status", path: "/dashboard/robots", icon: IC.robots },
                    { id: "map", label: "Live Map", path: "/dashboard/map", icon: IC.maps },
                    { id: "analytics", label: "Analytics", path: "/dashboard/analytics", icon: IC.analytics },
                ];

    const roleLabel =
        role === "admin"
            ? "Administrator"
            : role === "maintenance"
                ? "Maintenance Tech"
                : "Fleet Operator";

    return (
        <aside
            className={`h-full flex flex-col shrink-0 transition-all duration-300 ease-in-out ${isCollapsed ? "w-[80px]" : "w-55"
                }`}
            style={{
                background: "var(--surface-1)",
                borderRight: "1px solid var(--border-subtle)",
            }}
        >
            {/* Brand */}
            <div
                className="px-4 pt-5 pb-4 flex items-center justify-between"
                style={{ borderBottom: "1px solid var(--border-subtle)" }}
            >
                <div className="flex items-center gap-3 overflow-hidden cursor-default">
                    <div className="w-9 h-9 shrink-0 rounded-xl flex items-center justify-center relative overflow-hidden shadow-md">
                        <img
                            src="/icon/icon4.svg"
                            alt="SmartFleet Logo"
                            className="w-full h-full object-cover rounded-lg shadow-sm"
                        />
                    </div>
                    {!isCollapsed && (
                        <div className="whitespace-nowrap transition-opacity duration-300 opacity-100">
                            <div
                                className="font-bold text-[14px] leading-tight tracking-tight"
                                style={{ color: "var(--text-primary)" }}
                            >
                                SmartFleet
                            </div>
                            <div className="text-[10px] font-mono" style={{ color: "#22d3ee" }}>
                                WMS · v2.4.1
                            </div>
                        </div>
                    )}
                </div>

                {/* Collapse Button (Only shows when open) */}
                {!isCollapsed && (
                    <button
                        onClick={toggleCollapse}
                        className="w-7 h-7 rounded-lg flex items-center justify-center hover:bg-[var(--surface-hover)] transition-colors"
                        style={{ color: "var(--text-faint)" }}
                        title="Collapse Sidebar"
                    >
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                            <polyline points="15 18 9 12 15 6"></polyline>
                        </svg>
                    </button>
                )}
            </div>

            {/* Nav */}
            <nav className="flex-1 py-3 px-2 overflow-y-auto overflow-x-hidden space-y-0.5">
                {!isCollapsed && (
                    <div className="px-2 pb-2 pt-1 whitespace-nowrap">
                        <span
                            className="font-mono text-[9px] font-semibold tracking-widest"
                            style={{ color: "var(--text-dimmest)" }}
                        >
                            {role.toUpperCase()} NAVIGATION
                        </span>
                    </div>
                )}

                {navItems.map(({ id, label, path, icon }) => {
                    const active = location.pathname === path;
                    return (
                        <button
                            key={id}
                            onClick={() => navigate(path)}
                            title={isCollapsed ? label : undefined}
                            className={`w-full flex items-center rounded-lg text-[13px] font-medium transition-all relative hover:brightness-125 active:scale-[0.98] duration-200 ${isCollapsed ? "justify-center px-0 py-3" : "px-3 py-2.5 gap-3"
                                }`}
                            style={{
                                background: active ? "rgba(6,182,212,0.1)" : "transparent",
                                color: active ? "#22d3ee" : "var(--text-faint)",
                                border: active
                                    ? "1px solid rgba(6,182,212,0.2)"
                                    : "1px solid transparent",
                            }}
                        >
                            {active && (
                                <span
                                    className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 rounded-r-full"
                                    style={{ background: "#22d3ee" }}
                                />
                            )}
                            <span className="shrink-0" style={{ opacity: active ? 1 : 0.6 }}>
                                <Icon d={icon} size={18} />
                            </span>
                            {!isCollapsed && <span className="whitespace-nowrap">{label}</span>}
                        </button>
                    );
                })}
            </nav>

            {/* Broker widget (Hidden when collapsed) */}
            {!isCollapsed && (
                <div
                    className="mx-3 mb-3 rounded-xl p-3 transition-all hover:-translate-y-0.5 hover:shadow-md duration-300 whitespace-nowrap"
                    style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                >
                    <div className="flex items-center justify-between mb-2.5">
                        <span className="text-[10px] font-semibold font-mono" style={{ color: "var(--text-faint)" }}>
                            BROKER STATUS
                        </span>
                        <span className="flex items-center gap-1 text-[10px] font-mono" style={{ color: "#22c55e" }}>
                            <span className="w-1.5 h-1.5 rounded-full animate-pulse" style={{ background: "#22c55e" }} />
                            LIVE
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
            )}

            {/* User */}
            <div className="px-2 py-3" style={{ borderTop: "1px solid var(--border-subtle)" }}>
                <button
                    onClick={() => navigate("/profile")}
                    className={`w-full rounded-lg p-1 text-left transition-all hover:scale-[1.02] hover:brightness-125 active:scale-95 duration-200 flex ${isCollapsed ? "justify-center" : "items-center gap-2.5"
                        }`}
                >
                    <div
                        className="w-8 h-8 shrink-0 rounded-full flex items-center justify-center text-[11px] font-bold text-white shadow-sm"
                        style={{ background: "linear-gradient(135deg,#6366f1,#8b5cf6)" }}
                        title={isCollapsed ? userName : undefined}
                    >
                        {userInitials}
                    </div>
                    {!isCollapsed && (
                        <div className="min-w-0 flex-1 whitespace-nowrap">
                            <div className="text-[12px] font-semibold truncate" style={{ color: "var(--text-primary)" }}>
                                {userName}
                            </div>
                            <div className="font-mono text-[9px]" style={{ color: "var(--text-faint)" }}>
                                {roleLabel}
                            </div>
                        </div>
                    )}
                </button>

                {/* Theme toggle */}
                <button
                    onClick={toggleTheme}
                    title={isCollapsed ? "Toggle Theme" : undefined}
                    className={`mt-3 w-full py-2 rounded-lg text-[11px] font-semibold transition-all flex items-center justify-center gap-2 hover:-translate-y-0.5 hover:brightness-110 hover:shadow-sm active:translate-y-0 active:scale-95 duration-200 ${isCollapsed ? "px-0" : "px-2"
                        }`}
                    style={{
                        background: "var(--surface-3)",
                        color: "var(--text-secondary)",
                        border: "1px solid var(--border-medium)",
                    }}
                >
                    {theme === "dark" ? <Icon d={IC.sun} size={15} /> : <Icon d={IC.moon} size={15} />}
                    {!isCollapsed && <span className="whitespace-nowrap">{theme === "dark" ? "Switch to Light" : "Switch to Dark"}</span>}
                </button>

                {/* Sign Out */}
                <button
                    onClick={onLogout}
                    title={isCollapsed ? "Sign Out" : undefined}
                    className={`mt-2 w-full py-2 rounded-lg text-[11px] font-semibold transition-all hover:-translate-y-0.5 hover:brightness-110 hover:shadow-sm active:translate-y-0 active:scale-95 duration-200 flex items-center justify-center ${isCollapsed ? "px-0" : "px-2"
                        }`}
                    style={{
                        background: "rgba(239,68,68,0.1)",
                        color: "#f87171",
                        border: "1px solid rgba(239,68,68,0.2)",
                    }}
                >
                    {isCollapsed ? <Icon d={IC.x} size={15} /> : "Sign Out"}
                </button>
            </div>
        </aside>
    );
}