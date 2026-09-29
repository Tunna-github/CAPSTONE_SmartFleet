import { BrowserRouter, Routes, Route, Navigate, Outlet } from "react-router-dom";
import { Sidebar } from "./components/Sidebar";
import { LoginPage } from "./pages/LoginPage";
import { OperatorDashboard } from "./pages/OperatorDashboard";
import { TasksPage } from "./pages/TasksPage";
import { AdminDashboard } from "./pages/AdminDashboard";
import { RobotsPage } from "./pages/RobotsPage";
import { DataProvider } from "./context/DataContext";
import { ThemeProvider } from "./context/ThemeContext";
import { AuthProvider, useAuth, Role } from "./context/AuthContext";

function roleHome(role: Role): string {
    if (role === "admin") return "/admin";
    if (role === "maintenance") return "/dashboard/robots";
    return "/dashboard";
}

function ProtectedLayout() {
    const { isAuthenticated, role, logout } = useAuth();

    if (!isAuthenticated) return <Navigate to="/login" replace />;

    const handleLogout = async () => {
        await logout();
        window.location.href = "/login";
    };

    return (
        <div className="flex h-full overflow-hidden" style={{ background: "var(--background)" }}>
            <Sidebar role={role ?? "operator"} onLogout={handleLogout} />
            <div className="flex flex-col flex-1 min-w-0 overflow-hidden">
                <Outlet />
            </div>
        </div>
    );
}

function RootRedirect() {
    const { isAuthenticated, role } = useAuth();
    if (!isAuthenticated || !role) return <Navigate to="/login" replace />;
    return <Navigate to={roleHome(role)} replace />;
}

export default function App() {
    return (
        <ThemeProvider>
            <AuthProvider>
                <DataProvider>
                    <BrowserRouter>
                        <Routes>
                            <Route path="/login" element={<LoginPage />} />

                            <Route element={<ProtectedLayout />}>
                                <Route path="/dashboard" element={<OperatorDashboard />} />
                                <Route path="/dashboard/tasks" element={<TasksPage />} />
                                <Route path="/dashboard/robots" element={<RobotsPage />} />
                                <Route
                                    path="/dashboard/analytics"
                                    element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Analytics (Coming Soon)</div>}
                                />

                                <Route path="/admin" element={<AdminDashboard />} />
                                <Route
                                    path="/admin/robots"
                                    element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Admin Robots (Coming Soon)</div>}
                                />
                                <Route
                                    path="/admin/maps"
                                    element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Admin Maps (Coming Soon)</div>}
                                />
                            </Route>

                            <Route path="*" element={<RootRedirect />} />
                        </Routes>
                    </BrowserRouter>
                </DataProvider>
            </AuthProvider>
        </ThemeProvider>
    );
}