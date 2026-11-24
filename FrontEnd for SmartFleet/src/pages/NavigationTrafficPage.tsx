import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { useAuth } from "../context/AuthContext";
import { useWarehouseMap } from "../context/WarehouseMapContext";
import { WarehouseGrid } from "../components/WarehouseGrid";
import { PageHeader } from "../components/SharedUI";
import {
  getNavigationIntersections,
  navigationErrorMessage,
  simulateNavigationScan,
  type IntersectionTraffic,
} from "../api/modules/navigation";

const buttonStyle = {
  border: "1px solid var(--border-medium)",
  background: "var(--surface-3)",
  color: "var(--text-primary)",
};

function dateLabel(value: string): string {
  const ms = Date.parse(value);
  return Number.isFinite(ms) ? new Date(ms).toLocaleTimeString() : value;
}

export default function NavigationTrafficPage() {
  const { role } = useAuth();
  const { warehouseLayout, links, mapRevision, mapLoading, mapError, reloadMap } = useWarehouseMap();
  const [traffic, setTraffic] = useState<IntersectionTraffic[]>([]);
  const [trafficError, setTrafficError] = useState<string | null>(null);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const [robotCode, setRobotCode] = useState("R01");
  const [currentId, setCurrentId] = useState("1");
  const [nextId, setNextId] = useState("4");
  const [simBusy, setSimBusy] = useState(false);
  const [simResult, setSimResult] = useState<string | null>(null);
  const [simError, setSimError] = useState<string | null>(null);

  const refreshTraffic = useCallback(async () => {
    try {
      const list = await getNavigationIntersections();
      setTraffic(list);
      setUpdatedAt(new Date());
      setTrafficError(null);
    } catch (err) {
      setTrafficError(navigationErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    void refreshTraffic();
    // Live Backend polling; no simulated robot animation and no fake data.
    const timer = window.setInterval(() => void refreshTraffic(), 3000);
    return () => window.clearInterval(timer);
  }, [refreshTraffic]);

  const qrMarkers = useMemo(() => warehouseLayout.flatMap(row => row)
    .filter(c => c.type === "qr" && c.arucoId !== undefined)
    .map(c => ({ label: c.label ?? `QR${c.arucoId}`, id: c.arucoId! }))
    .sort((a, b) => a.id - b.id), [warehouseLayout]);

  const approachingTotal = traffic.reduce((n, x) => n + x.approachingCount, 0);
  const insideTotal = traffic.reduce((n, x) => n + x.insideCount, 0);
  const waitingTotal = traffic.reduce((n, x) => n + x.waitingCount, 0);

  const onSimulate = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSimError(null);
    setSimResult(null);
    const from = Number(currentId);
    const to = Number(nextId);
    if (!robotCode.trim() || !Number.isInteger(from) || !Number.isInteger(to) || from === to) {
      setSimError("Enter a robot code and two different numeric ArUco IDs.");
      return;
    }
    setSimBusy(true);
    try {
      await simulateNavigationScan(robotCode, from, to);
      await refreshTraffic();
      setSimResult(`${robotCode.trim()} previewed QR${from} -> QR${to}. Live traffic and robot commands were unchanged.`);
    } catch (error) {
      setSimError(navigationErrorMessage(error));
    } finally {
      setSimBusy(false);
    }
  };

  return (
    <div className="h-full overflow-auto p-6 space-y-5" style={{ color: "var(--text-primary)" }}>
      <PageHeader title="Intersection Traffic" sub="Live navigation state reported by the Backend (not simulated robot positions)" />

      <div className="flex flex-wrap gap-2 items-center">
        <span className="text-xs" style={{ color: "var(--text-faint)" }}>
          Map revision: {mapRevision ?? "not loaded"} {mapLoading ? "· loading..." : ""}
        </span>
        <button className="rounded-lg px-3 py-2 text-xs font-semibold" style={buttonStyle}
          onClick={() => void refreshTraffic()}>Refresh traffic</button>
        <button className="rounded-lg px-3 py-2 text-xs font-semibold" style={buttonStyle}
          onClick={() => void reloadMap().catch(() => {})}>Reload map from Backend</button>
        {updatedAt && <span className="text-xs" style={{ color: "var(--text-faint)" }}>
          Updated at {updatedAt.toLocaleTimeString()} · polls every 3 seconds
        </span>}
      </div>

      {(trafficError || mapError) && (
        <div role="alert" className="rounded-xl border px-4 py-3 text-sm"
          style={{ borderColor: "#ef4444", color: "#f87171", background: "var(--surface-2)" }}>
          {trafficError && <p>Traffic API: {trafficError}</p>}
          {mapError && <p>Map API: {mapError}</p>}
        </div>
      )}

      <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
        {[
          ["Intersections", traffic.length],
          ["Approaching", approachingTotal],
          ["Waiting", waitingTotal],
          ["Inside", insideTotal],
        ].map(([label, value]) => (
          <div key={label} className="rounded-xl border p-4" style={{ background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}>
            <div className="text-xs" style={{ color: "var(--text-faint)" }}>{label}</div>
            <div className="text-2xl font-bold mt-1">{value}</div>
          </div>
        ))}
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-[minmax(0,1.5fr)_minmax(310px,1fr)] gap-4">
        <section className="rounded-xl border overflow-hidden" style={{ background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}>
          <div className="px-4 py-3 font-semibold text-sm">Backend Warehouse Map</div>
          <div style={{ height: 450 }}>
            <WarehouseGrid robots={[]} animateRobots={false} grid={warehouseLayout} links={links} />
          </div>
          <div className="px-4 py-3 text-xs" style={{ color: "var(--text-faint)" }}>
            Map geometry comes from <code>GET /api/v1/navigation/map</code>. No artificial robot movement is shown.
          </div>
        </section>
        <section className="rounded-xl border p-4 space-y-3" style={{ background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}>
          <div className="font-semibold text-sm">Intersection queue</div>
          {traffic.length === 0 && <p className="text-sm" style={{ color: "var(--text-faint)" }}>
            No intersections configured. Use Warehouse Maps to add a QR marker and mark it as an intersection, then Save to Backend.
          </p>}
          {traffic.map(item => (
            <div key={item.intersectionArUcoId} className="rounded-lg border p-3 space-y-2" style={{ borderColor: "var(--border-medium)" }}>
              <div className="flex flex-wrap gap-2 justify-between items-center">
                <strong>QR{item.intersectionArUcoId}</strong>
                <span className="text-xs" style={{ color: item.blockedPendingPhysicalClearance ? "#f87171" : "var(--text-faint)" }}>
                  {item.blockedPendingPhysicalClearance ? "PHYSICAL CLEARANCE REQUIRED" : item.activeRobotCode ? `Active: ${item.activeRobotCode}` : "No active grant"}
                </span>
              </div>
              <div className="text-xs" style={{ color: "var(--text-muted)" }}>
                Approaching: {item.approachingCount} · Waiting: {item.waitingCount} · Inside: {item.insideCount}
              </div>
              {item.approachingRobots.map(robot => (
                <div key={robot.robotCode} className="rounded-md p-2 text-xs" style={{ background: "var(--surface-3)" }}>
                  <strong>{robot.robotCode}</strong> · QR{robot.currentArUcoId} → QR{robot.nextArUcoId}
                  <span className="ml-2" style={{ color: robot.phase === "WAITING" ? "#fbbf24" : "#34d399" }}>{robot.phase}</span>
                  <div className="mt-1" style={{ color: "var(--text-faint)" }}>Last report: {dateLabel(robot.lastReportedAtUtc)}</div>
                </div>
              ))}
              {item.approachingRobots.length === 0 && <p className="text-xs" style={{ color: "var(--text-faint)" }}>No recent approaches.</p>}
            </div>
          ))}
        </section>
      </div>

      {role === "admin" && (
        <section className="rounded-xl border p-4 space-y-3" style={{ background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}>
          <h2 className="font-semibold text-sm">Safe Backend scan simulation</h2>
          <p className="text-xs" style={{ color: "var(--text-muted)" }}>
            Calls <code>POST /api/v1/navigation/robots/&#123;robotCode&#125;/scan?simulate=true</code>.
            Requires a Development Backend, Administrator login, a registered active robot, and connected QR nodes.
            This request does not publish MQTT navigation directives.
          </p>
          <form onSubmit={(event) => void onSimulate(event)} className="flex flex-wrap gap-3 items-end">
            <label className="text-xs flex flex-col gap-1">Robot code
              <input className="rounded-lg border px-3 py-2 min-w-28" style={buttonStyle}
                value={robotCode} onChange={event => setRobotCode(event.target.value)} placeholder="R01" />
            </label>
            <label className="text-xs flex flex-col gap-1">Current ArUco
              <select className="rounded-lg border px-3 py-2" style={buttonStyle}
                value={currentId} onChange={event => setCurrentId(event.target.value)}>
                {!qrMarkers.some(q => String(q.id) === currentId) && <option value={currentId}>{currentId} (not on map)</option>}
                {qrMarkers.map(q => <option key={q.id} value={q.id}>{q.label} (ID {q.id})</option>)}
              </select>
            </label>
            <label className="text-xs flex flex-col gap-1">Next ArUco
              <select className="rounded-lg border px-3 py-2" style={buttonStyle}
                value={nextId} onChange={event => setNextId(event.target.value)}>
                {!qrMarkers.some(q => String(q.id) === nextId) && <option value={nextId}>{nextId} (not on map)</option>}
                {qrMarkers.map(q => <option key={q.id} value={q.id}>{q.label} (ID {q.id})</option>)}
              </select>
            </label>
            <button disabled={simBusy} type="submit" className="px-4 py-2 rounded-lg font-semibold text-xs disabled:opacity-50"
              style={{ background: "#0d9488", color: "white" }}>
              {simBusy ? "Simulating..." : "Simulate Scan (NO MQTT)"}
            </button>
          </form>
          {simError && <p role="alert" className="text-xs" style={{ color: "#f87171" }}>{simError}</p>}
          {simResult && <p role="status" className="text-xs" style={{ color: "#34d399" }}>{simResult}</p>}
        </section>
      )}
    </div>
  );
}
