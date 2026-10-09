import { BrowserRouter, Routes, Route, Navigate, Outlet } from "react-router-dom";
import { Sidebar } from "./components/Sidebar";
import { LoginPage } from "./pages/LoginPage";
import { OperatorDashboard } from "./pages/OperatorDashboard";
import { TasksPage } from "./pages/TasksPage";
import { AdminDashboard } from "./pages/AdminDashboard";
import { RobotsPage } from "./pages/RobotsPage";
import { UserProfilePage } from "./pages/UserProfilePage";
import { AdminUsersPage } from "./pages/AdminUsersPage";
import { DataProvider } from "./context/DataContext";
import { ThemeProvider } from "./context/ThemeContext";
import { AuthProvider, useAuth, Role } from "./context/AuthContext";

function roleHome(role: Role): string {
    if (role === "admin") return "/admin";
    if (role === "maintenance") return "/dashboard/robots";
    return "/dashboard";
}

function ProtectedLayout() {
    const { isAuthenticated, role, logout, isLoading } = useAuth();

    if (isLoading) return <AuthScreen message="Checking current session..." />;

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
    const { isAuthenticated, role, isLoading } = useAuth();
    if (isLoading) return <AuthScreen message="Checking current session..." />;
    if (!isAuthenticated || !role) return <Navigate to="/login" replace />;
    return <Navigate to={roleHome(role)} replace />;
}

function RequireRole({ allow }: { allow: Role[] }) {
    const { role, isLoading } = useAuth();
    if (isLoading) return <AuthScreen message="Checking current session..." />;
    if (!role || !allow.includes(role)) {
        return <Navigate to={role ? roleHome(role) : "/login"} replace />;
    }
    return <Outlet />;
}

function AuthScreen({ message }: { message: string }) {
    return (
        <div
            className="min-h-screen flex items-center justify-center p-4"
            style={{ background: "var(--background)", color: "var(--text-primary)" }}
        >
            <div
                className="rounded-2xl px-6 py-5 text-center"
                style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
            >
                <div className="text-[15px] font-bold">SmartFleet WMS</div>
                <div className="mt-2 text-[12px]" style={{ color: "var(--text-faint)" }}>{message}</div>
            </div>
        </div>
    );
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
                                <Route path="/profile" element={<UserProfilePage />} />
                                <Route element={<RequireRole allow={["operator", "admin"]} />}>
                                    <Route path="/dashboard" element={<OperatorDashboard />} />
                                    <Route path="/dashboard/tasks" element={<TasksPage />} />
                                    <Route
                                        path="/dashboard/analytics"
                                        element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Analytics (Coming Soon)</div>}
                                    />
                                </Route>

                                <Route element={<RequireRole allow={["operator", "admin", "maintenance"]} />}>
                                    <Route path="/dashboard/robots" element={<RobotsPage />} />
                                </Route>

                                <Route element={<RequireRole allow={["admin"]} />}>
                                    <Route path="/admin" element={<AdminDashboard />} />
                                    <Route path="/admin/users" element={<AdminUsersPage />} />
                                    <Route
                                        path="/admin/robots"
                                        element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Admin Robots (Coming Soon)</div>}
                                    />
                                    <Route
                                        path="/admin/maps"
                                        element={<div className="p-6" style={{ color: "var(--text-primary)" }}>Admin Maps (Coming Soon)</div>}
                                    />
                                </Route>
                            </Route>

                            <Route path="*" element={<RootRedirect />} />
                        </Routes>
                    </BrowserRouter>
                </DataProvider>
            </AuthProvider>
        </ThemeProvider>
    );
}
