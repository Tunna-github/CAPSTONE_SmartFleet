import { useState } from "react";
import { BrowserRouter, Routes, Route, Navigate, Outlet } from "react-router-dom";
import { Sidebar } from "./components/Sidebar";
import { LoginPage } from "./pages/LoginPage";
import { OperatorDashboard } from "./pages/OperatorDashboard";
import { TaskCreationPage } from "./pages/TaskCreationPage";
import { AdminDashboard } from "./pages/AdminDashboard";
import { RobotsPage } from "./pages/RobotsPage";
import { DataProvider } from "./context/DataContext";

function DashboardLayout({ role, onLogout }: { role: "operator" | "admin", onLogout: () => void }) {
    return (
        <div className="flex h-full overflow-hidden" style={{ background: "#070d1e" }}>
            <Sidebar role={role} onLogout={onLogout} />
            <div className="flex flex-col flex-1 min-w-0 overflow-hidden">
                <Outlet />
            </div>
        </div>
    );
}

export default function App() {
    const [role, setRole] = useState<"operator" | "admin" | null>(null);
    const handleLogin = (newRole: "operator" | "admin") => setRole(newRole);
    const handleLogout = () => setRole(null);

    return (
        <DataProvider>
            <BrowserRouter>
                <Routes>
                    <Route path="/login" element={role ? <Navigate to={role === "admin" ? "/admin" : "/dashboard"} replace /> : <LoginPage onLogin={handleLogin} />} />

                    <Route element={role ? <DashboardLayout role={role} onLogout={handleLogout} /> : <Navigate to="/login" replace />}>
                        <Route path="/dashboard" element={<OperatorDashboard />} />
                        <Route path="/dashboard/create-task" element={<TaskCreationPage />} />
                        <Route path="/dashboard/robots" element={<RobotsPage />} />
                        <Route path="/dashboard/analytics" element={<div className="p-6 text-white">Analytics Page (Coming Soon)</div>} />

                        <Route path="/admin" element={<AdminDashboard />} />
                        <Route path="/admin/robots" element={<div className="p-6 text-white">Admin Robots Page (Coming Soon)</div>} />
                        <Route path="/admin/maps" element={<div className="p-6 text-white">Admin Maps Page (Coming Soon)</div>} />
                    </Route>

                    <Route path="*" element={<Navigate to={role ? (role === "admin" ? "/admin" : "/dashboard") : "/login"} replace />} />
                </Routes>
            </BrowserRouter>
        </DataProvider>
    );
}