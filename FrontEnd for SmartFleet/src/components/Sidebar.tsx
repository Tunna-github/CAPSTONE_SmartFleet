import { Icon, IC } from "./Icons";
import { useNavigate, useLocation } from "react-router-dom";
import { useTheme } from "../context/ThemeContext";
import { useAuth, type Role } from "../context/AuthContext";
import RobotSafetyPanel from "./RobotSafetyPanel";
import NotificationPanel from "./NotificationPanel";
import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import fleetLogo from "../../Icon/icon4.svg";

export function Sidebar({ role, onLogout, isCollapsed, toggleCollapse }: {
    role: Role; onLogout: () => void; isCollapsed: boolean; toggleCollapse: () => void;
}) {
    const navigate = useNavigate();
    const location = useLocation();
    const { theme, toggleTheme } = useTheme();
    const { user } = useAuth();
    const [health, setHealth] = useState<{ mqtt?: string; onlineRobotCount?: number; robotCount?: number } | null>(null);
    useEffect(() => {
        let disposed = false;
        const refresh = async () => {
            try { const { data } = await apiClient.get("/api/v1/system/health"); if (!disposed) setHealth(data); }
            catch { if (!disposed) setHealth(null); }
        };
        void refresh(); const timer = window.setInterval(() => void refresh(), 10000);
        return () => { disposed = true; window.clearInterval(timer); };
    }, [user?.userId]);
    const userName = user?.fullName || user?.username || "SmartFleet User";
    const initials = userName.trim().split(/\s+/).slice(0, 2).map(p => p[0]?.toUpperCase() ?? "").join("") || "SF";
    const admin = role === "admin", maintenance = role === "maintenance";
    const operations = [
        { label: admin ? "Admin Dashboard" : "Dashboard", path: admin ? "/admin" : "/dashboard", icon: IC.dashboard },
        { label: "Live tracking & handover", path: "/simulation", icon: IC.maps },
        ...(!admin && !maintenance ? [{ label: "Transport tasks", path: "/dashboard/tasks", icon: IC.tasks }] : []),
        { label: "Fleet Status", path: "/dashboard/robots", icon: IC.robots },
        ...(!maintenance ? [
            { label: "Mission history", path: admin ? "/admin/qr-missions" : "/dashboard/qr-missions", icon: IC.maps },
            { label: "Intersection Traffic", path: admin ? "/admin/navigation" : "/dashboard/navigation", icon: IC.maps },
            { label: "Analytics & Reports", path: admin ? "/admin/analytics" : "/dashboard/analytics", icon: IC.analytics },
        ] : [{ label: "Maintenance", path: "/dashboard/maintenance", icon: IC.settings }]),
    ].filter(item => !maintenance || item.path !== "/dashboard");
    const management = admin ? [
        { label: "Warehouse Maps", path: "/admin/maps", icon: IC.maps },
        { label: "Robot Registry", path: "/admin/robots", icon: IC.robots },
        { label: "User Management", path: "/admin/users", icon: IC.users },
    ] : [{ label: "Station Operations", path: "/warehouse-stations", icon: IC.maps }];
    const roleLabel = admin ? "Administrator" : maintenance ? "Maintenance Technician" : "Warehouse Operator";
    const go = (path: string) => { navigate(path); if (!isCollapsed && window.matchMedia("(max-width: 640px)").matches) toggleCollapse(); };
    return <>
        {!isCollapsed && <button className="sidebar-scrim" aria-label="Close navigation" onClick={toggleCollapse} />}
        <aside className={`sf-sidebar ${isCollapsed ? "is-collapsed" : ""}`} aria-label="Main navigation">
            <div className="sidebar-brand">
                <button onClick={toggleCollapse} aria-label={isCollapsed ? "Open Sidebar" : "Collapse Sidebar"} title={isCollapsed ? "Open Sidebar" : "Collapse Sidebar"} className="sidebar-logo"><img src={fleetLogo} alt="SmartFleet Logo" /></button>
                {!isCollapsed && <><div className="flex-1"><strong>SmartFleet</strong><span>Warehouse operations</span></div><button aria-label="Collapse navigation" className="sidebar-collapse" onClick={toggleCollapse}>‹</button></>}
            </div>
            <nav className="sidebar-links" aria-label="Workspace navigation">
                {[{ title: "OPERATIONS", items: operations }, { title: admin ? "ADMINISTRATION" : "WAREHOUSE", items: management }].map(group => <div className="sidebar-group" key={group.title}>
                    {!isCollapsed && <p className="sf-eyebrow">{group.title}</p>}
                    {group.items.map(item => <button key={item.path} onClick={() => go(item.path)} title={isCollapsed ? item.label : undefined} aria-label={item.label} aria-current={location.pathname === item.path ? "page" : undefined}>
                        <Icon d={item.icon} size={18} />{!isCollapsed && <span>{item.label}</span>}
                    </button>)}
                </div>)}
            </nav>
            <div className="sidebar-safety"><RobotSafetyPanel isCollapsed={isCollapsed} /></div>
            <NotificationPanel collapsed={isCollapsed} />
            {!isCollapsed && <div className="sidebar-health"><span><i className={`status-dot ${health?.mqtt === "CONNECTED" ? "online" : ""}`} />MQTT {health?.mqtt === "CONNECTED" ? "connected" : health?.mqtt ?? "unknown"}</span><strong>{health?.robotCount !== undefined ? `${health.onlineRobotCount} / ${health.robotCount} online` : "—"}</strong></div>}
            <div className="sidebar-account">
                <button className="sidebar-profile" onClick={() => go("/profile")} aria-label="User profile" title={isCollapsed ? userName : undefined}><span className="sidebar-avatar">{initials}</span>{!isCollapsed && <span><strong>{userName}</strong><small>{roleLabel}</small></span>}</button>
                <div className="sidebar-account-actions"><button onClick={toggleTheme} title={theme === "dark" ? "Switch to Light" : "Switch to Dark"} aria-label={theme === "dark" ? "Switch to Light" : "Switch to Dark"}><Icon d={theme === "dark" ? IC.sun : IC.moon} size={15} />{!isCollapsed && <span>{theme === "dark" ? "Light mode" : "Dark mode"}</span>}</button><button onClick={onLogout} aria-label="Sign Out" title="Sign Out"><Icon d={IC.x} size={15} />{!isCollapsed && <span>Sign Out</span>}</button></div>
            </div>
        </aside>
    </>;
}
