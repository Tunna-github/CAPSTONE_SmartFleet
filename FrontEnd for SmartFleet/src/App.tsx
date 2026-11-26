import { useEffect, useState } from "react"
import WarehouseMapProvider from "./context/WarehouseMapContext"

import {
  BrowserRouter,
  Routes,
  Route,
  Navigate,
  Outlet,
  useLocation,
} from "react-router-dom"

import { Sidebar } from "./components/Sidebar"

import { LoginPage } from "./pages/LoginPage"

import { OperatorDashboard } from "./pages/OperatorDashboard"

import { TasksPage } from "./pages/TasksPage"

import { AdminDashboard } from "./pages/AdminDashboard"
import RobotRegistryPage from "./pages/RobotRegistryPage"
import AnalyticsPage from "./pages/AnalyticsPage"

import WarehouseMapsPage from "./pages/WarehouseMapsPage"
import QrMissionMonitorPage from "./pages/QrMissionMonitorPage"
import NavigationTrafficPage from "./pages/NavigationTrafficPage"
import SimulationPage from "./pages/SimulationPage"
import WarehouseStationsPage from "./pages/WarehouseStationsPage"
import { RobotsPage } from "./pages/RobotsPage"

import { UserProfilePage } from "./pages/UserProfilePage"

import { AdminUsersPage } from "./pages/AdminUsersPage"

import { DataProvider } from "./context/DataContext"

import { ThemeProvider } from "./context/ThemeContext"

import { AuthProvider, useAuth, type Role } from "./context/AuthContext"

function roleHome(role: Role): string {
  if (role === "admin") return "/admin"

  if (role === "maintenance") return "/dashboard/robots"

  return "/dashboard"
}

function ProtectedLayout() {
  const { isAuthenticated, role, logout, isLoading } = useAuth()

  // State to track if the sidebar is collapsed

  const [isCollapsed, setIsCollapsed] = useState(() => window.matchMedia("(max-width: 640px)").matches)
  const location = useLocation()
  const trackingVisible = location.pathname.replace(/\/+$/, "") === "/simulation"
  const [trackingOpened, setTrackingOpened] = useState(trackingVisible)
  useEffect(() => { if (trackingVisible) setTrackingOpened(true) }, [trackingVisible])

  if (isLoading) {
    return <AuthScreen message="Checking current session..." />
  }

  if (!isAuthenticated) {
    return <Navigate to="/login" replace />
  }

  const handleLogout = async () => {
    await logout()

    window.location.href = "/login"
  }

  return (
    <div
      className="sf-app-shell flex h-full overflow-hidden"
      style={{ background: "var(--background)" }}
    >
      <Sidebar
        role={role ?? "operator"}
        onLogout={handleLogout}
        isCollapsed={isCollapsed}
        toggleCollapse={() => setIsCollapsed(!isCollapsed)}
      />

      <div className="flex flex-col flex-1 min-w-0 overflow-hidden relative">
        {/* The viewer belongs to the authenticated shell, not to a route/tab lifecycle.
            Leaving tracking hides it; logout still disposes the entire session. */}
        {(trackingOpened || trackingVisible) && <div style={{ display: trackingVisible ? "flex" : "none" }} className="flex-1 min-h-0 min-w-0 flex-col" aria-hidden={!trackingVisible}>
          <SimulationPage visible={trackingVisible} />
        </div>}
        <div style={{ display: trackingVisible ? "none" : "flex" }} className="flex-1 min-h-0 min-w-0 flex-col"><Outlet /></div>
      </div>
    </div>
  )
}

function RootRedirect() {
  const { isAuthenticated, role, isLoading } = useAuth()

  if (isLoading) {
    return <AuthScreen message="Checking current session..." />
  }

  if (!isAuthenticated || !role) {
    return <Navigate to="/login" replace />
  }

  return <Navigate to={roleHome(role)} replace />
}

function RequireRole({ allow }: { allow: Role[] }) {
  const { role, user, isLoading } = useAuth()

  if (isLoading) {
    return <AuthScreen message="Checking current session..." />
  }

  const names: Record<Role, string> = { admin: "Administrator", operator: "Warehouse Operator", maintenance: "Maintenance Technician" }
  if (!role || !allow.some(r => user?.roles?.includes(names[r]))) {
    return <Navigate to={role ? roleHome(role) : "/login"} replace />
  }

  return <Outlet />
}

function AuthScreen({ message }: { message: string }) {
  return (
    <div
      className="min-h-screen flex items-center justify-center p-4"
      style={{
        background: "var(--background)",

        color: "var(--text-primary)",
      }}
    >
      <div
        className="rounded-2xl px-6 py-5 text-center"
        style={{
          background: "var(--surface-2)",

          border: "1px solid var(--border-subtle)",
        }}
      >
        <div className="text-[15px] font-bold">SmartFleet WMS</div>

        <div
          className="mt-2 text-[12px]"
          style={{ color: "var(--text-faint)" }}
        >
          {message}
        </div>
      </div>
    </div>
  )
}

export default function App() {
  return (
    <ThemeProvider>
      <AuthProvider>
        <DataProvider>
          <WarehouseMapProvider>
          <BrowserRouter>
            <Routes>
              <Route path="/login" element={<LoginPage />} />

              <Route element={<ProtectedLayout />}>
                <Route path="/profile" element={<UserProfilePage />} />
                <Route path="/simulation" element={null} />
                <Route path="/warehouse-stations" element={<WarehouseStationsPage />} />

                <Route element={<RequireRole allow={["operator"]} />}>
                  <Route path="/dashboard" element={<OperatorDashboard />} />

                  <Route path="/dashboard/tasks" element={<TasksPage />} />

                </Route>

                <Route element={<RequireRole allow={["operator", "admin"]} />}>
                  <Route path="/dashboard/analytics" element={<AnalyticsPage />} />
                  <Route path="/admin/analytics" element={<AnalyticsPage />} />
                  <Route path="/dashboard/qr-missions" element={<QrMissionMonitorPage />} />
                  <Route path="/dashboard/navigation" element={<NavigationTrafficPage />} />
                </Route>

                <Route
                  element={
                    <RequireRole
                      allow={[
                        "operator",

                        "admin",

                        "maintenance",
                      ]}
                    />
                  }
                >
                  <Route path="/dashboard/robots" element={<RobotsPage />} />
                </Route>

                <Route
                  element={<RequireRole allow={["maintenance", "admin"]} />}
                >
                  <Route
                    path="/dashboard/maintenance"
                    element={<RobotsPage />}
                  />
                </Route>

                <Route element={<RequireRole allow={["admin"]} />}>
                  <Route path="/admin" element={<AdminDashboard />} />

                  <Route path="/admin/users" element={<AdminUsersPage />} />

                  <Route
                    path="/admin/robots"
                    element={
                      <RobotRegistryPage />
                    }
                  />

                  <Route path="/admin/maps" element={<WarehouseMapsPage />} />
                  <Route path="/admin/qr-missions" element={<QrMissionMonitorPage />} />
                  <Route path="/admin/navigation" element={<NavigationTrafficPage />} />
                </Route>
              </Route>

              <Route path="*" element={<RootRedirect />} />
            </Routes>
          </BrowserRouter>
          </WarehouseMapProvider>
        </DataProvider>
      </AuthProvider>
    </ThemeProvider>
  )
}
