import { useState, useEffect, useMemo } from "react"

import { useData } from "../context/DataContext"
import { useWarehouseMap } from "../context/WarehouseMapContext"
import { WarehouseGrid } from "../components/WarehouseGrid"
import { Icon, IC } from "../components/Icons"
import { PageHeader, PrimaryBtn } from "../components/SharedUI"

// ─── TYPES & INTERFACES ──────────────────────────────────────────────────────

interface RobotAsset {
  id: string
  model: string
  mac: string
  ip: string
  dateAdded: string
  firmware: string
  maintenance: boolean
  mqtt: "ONLINE" | "OFFLINE"
}

// ─── SEED DATA ───────────────────────────────────────────────────────────────
const SEED_ROBOT_ASSETS: RobotAsset[] = [
  {
    id: "AMR-001",
    model: "FleetBot X200",
    mac: "A4:C3:F0:85:AC:01",
    ip: "192.168.1.101",
    dateAdded: "2025-03-12",
    firmware: "v3.2.1",
    maintenance: false,
    mqtt: "ONLINE",
  },
  {
    id: "AMR-002",
    model: "FleetBot X200",
    mac: "A4:C3:F0:85:AC:02",
    ip: "192.168.1.102",
    dateAdded: "2025-03-12",
    firmware: "v3.2.1",
    maintenance: false,
    mqtt: "ONLINE",
  },
]

const MODEL_OPTIONS = [
  "FleetBot X100",
  "FleetBot X200",
  "FleetBot X300",
  "FleetBot X400 (Beta)",
]

// ─── MAIN COMPONENT ──────────────────────────────────────────────────────────
export function AdminDashboard() {
  const { toggleMaintenance, robots: fleetRobots } = useData()
  const { warehouseLayout, links } = useWarehouseMap()
  const [search, setSearch] = useState("")
  const [toast, setToast] = useState<{
    msg: string
    type: "success" | "error" | "info"
  } | null>(null)

  const [robots, setRobots] = useState<RobotAsset[]>(SEED_ROBOT_ASSETS)
  const [addRobotOpen, setAddRobotOpen] = useState(false)

  // Add robot form
  const [newRId, setNewRId] = useState("")
  const [newRModel, setNewRModel] = useState(MODEL_OPTIONS[1])
  const [newRMac, setNewRMac] = useState("")
  const [newRIp, setNewRIp] = useState("")

  // --- Handlers: Robot ---
  const saveNewRobot = () => {
    if (!newRId.trim() || !newRMac.trim()) {
      setToast({ msg: "Robot ID and MAC are required.", type: "error" })
      return
    }
    setRobots((prev) => [
      ...prev,
      {
        id: newRId.trim(),
        model: newRModel,
        mac: newRMac.trim(),
        ip: newRIp.trim() || "0.0.0.0",
        dateAdded: new Date().toISOString().slice(0, 10),
        firmware: "v4.0.0",
        maintenance: false,
        mqtt: "OFFLINE",
      },
    ])
    setAddRobotOpen(false)
    setNewRId("")
    setNewRMac("")
    setNewRIp("")
  }

  const handleToggleMaintenance = (id: string) => {
    setRobots((prev) =>
      prev.map((r) =>
        r.id === id
          ? {
              ...r,
              maintenance: !r.maintenance,
              mqtt: !r.maintenance ? "OFFLINE" : "ONLINE",
            }
          : r,
      ),
    )
    toggleMaintenance(id)
  }

  const filteredRobots = robots.filter(
    (r) =>
      !search ||
      r.id.toLowerCase().includes(search.toLowerCase()) ||
      r.mac.toLowerCase().includes(search.toLowerCase()),
  )

  const mapRobots = useMemo(() => {
    const points: Array<{ x: number; y: number }> = []
    warehouseLayout.forEach((row, y) => row.forEach((cell, x) => {
      if (cell.type === "station") points.push({ x, y })
    }))
    if (!points.length) return []
    return fleetRobots.map((robot, index) => {
      const from = points[index % points.length]
      const to = points[(index + 3) % points.length]
      return {
        id: robot.id,
        x: from.x,
        y: from.y,
        tx: to.x,
        ty: to.y,
        battery: robot.battery,
        mqtt: robot.mqtt,
        missionId: robot.maintenance ? null : robot.missionId,
        color: robot.mqtt === "ONLINE" ? "#22d3ee" : "#ef4444",
      }
    })
  }, [fleetRobots, warehouseLayout])

  return (
    <div
      className="flex flex-col h-full overflow-hidden transition-colors"
      style={{ background: "var(--background)" }}
    >
      <PageHeader title="Admin Dashboard" sub="Fleet overview and robot assets">
        <PrimaryBtn onClick={() => setAddRobotOpen(true)}>
          <Icon d={IC.plus} size={15} /> Add Robot
        </PrimaryBtn>
      </PageHeader>

      {/* View Area */}
      <div className="flex-1 min-h-0 overflow-y-auto px-6 pb-6 pt-4 space-y-4">
        <section
          className="rounded-2xl overflow-hidden border"
          style={{ background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}
        >
          <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3">
            <h2 className="text-sm font-semibold" style={{ color: "var(--text-primary)" }}>
              Warehouse Fleet Map
            </h2>
            <span className="text-xs font-mono" style={{ color: "#f59e0b" }}>
              SIMULATION - not live telemetry
            </span>
          </div>
          <div style={{ height: 520 }}>
            <WarehouseGrid robots={mapRobots} grid={warehouseLayout} links={links} />
          </div>
          <p className="px-4 py-3 text-xs" style={{ color: "var(--text-faint)" }}>
            Read-only view. Online robots with a mission move along available routes.
            Edit stations, QR markers and routes in Warehouse Maps.
          </p>
        </section>
        <div
          className="rounded-2xl overflow-hidden flex flex-col transition-colors"
          style={{
            background: "var(--surface-2)",
            border: "1px solid var(--border-subtle)",
          }}
        >
          <div className="overflow-auto flex-1">
            <RobotTable
              robots={filteredRobots}
              onToggle={handleToggleMaintenance}
            />
          </div>
        </div>
      </div>

      {toast && (
        <ToastEl
          msg={toast.msg}
          type={toast.type}
          onDone={() => setToast(null)}
        />
      )}
    </div>
  )
}

// UI subcomponents
function RobotTable({
  robots,
  onToggle,
}: {
  robots: RobotAsset[]
  onToggle: (id: string) => void
}) {
  return (
    <table className="w-full border-collapse" style={{ minWidth: 800 }}>
      <thead>
        <tr
          style={{
            borderBottom: "1px solid var(--border-subtle)",
            background: "var(--surface-1)",
          }}
        >
          {["Robot ID", "Model", "MAC Address", "MQTT", "Maintenance"].map(
            (h) => (
              <th
                key={h}
                className="text-left px-4 py-3 font-mono text-[10px] font-semibold tracking-wider whitespace-nowrap"
                style={{ color: "var(--text-faint)" }}
              >
                {h}
              </th>
            ),
          )}
        </tr>
      </thead>
      <tbody>
        {robots.map((r) => (
          <tr
            key={r.id}
            style={{ borderBottom: "1px solid var(--border-subtle)" }}
          >
            <td
              className="px-4 py-3 font-mono text-[12px] font-bold"
              style={{ color: "#22d3ee" }}
            >
              {r.id}
            </td>
            <td
              className="px-4 py-3 text-[12px]"
              style={{ color: "var(--text-secondary)" }}
            >
              {r.model}
            </td>
            <td
              className="px-4 py-3 font-mono text-[11px]"
              style={{ color: "var(--text-muted)" }}
            >
              {r.mac}
            </td>
            <td className="px-4 py-3">
              <span
                className="inline-flex items-center gap-1.5 font-mono text-[10px] font-bold px-2 py-1 rounded"
                style={{
                  background:
                    r.mqtt === "ONLINE"
                      ? "rgba(34,197,94,0.1)"
                      : "rgba(239,68,68,0.1)",
                  color: r.mqtt === "ONLINE" ? "#22c55e" : "#ef4444",
                  border: `1px solid ${
                    r.mqtt === "ONLINE"
                      ? "rgba(34,197,94,0.25)"
                      : "rgba(239,68,68,0.25)"
                  }`,
                }}
              >
                {r.mqtt}
              </span>
            </td>
            <td className="px-4 py-3">
              <button
                onClick={() => onToggle(r.id)}
                className="px-3 py-1 rounded-md text-[10px] font-bold"
                style={{
                  background: r.maintenance ? "#f59e0b" : "var(--surface-3)",
                  color: r.maintenance ? "#fff" : "var(--text-muted)",
                }}
              >
                {r.maintenance ? "MAINTENANCE" : "READY"}
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function ToastEl({ msg, type, onDone }: any) {
  useEffect(() => {
    const t = setTimeout(() => {
      onDone()
    }, 3000)
    return () => clearTimeout(t)
  }, [onDone])
  return (
    <div
      className="fixed bottom-6 right-6 z-[200] px-4 py-3 rounded-xl border border-gray-700 shadow-2xl"
      style={{ background: "var(--surface-2)", color: "#fff" }}
    >
      {msg}
    </div>
  )
}
