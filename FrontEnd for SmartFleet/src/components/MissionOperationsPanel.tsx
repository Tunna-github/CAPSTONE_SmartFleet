import { Link } from "react-router-dom";
import { useAuth } from "../context/AuthContext";
import { useCatalog } from "../api/modules/catalog";
import MissionVerificationPanel from "./MissionVerificationPanel";
import type { TrackingMission } from "../hooks/useMissionWorkspace";

export function needsTransfer(m: TrackingMission) {
    return m.assignmentStatus === "ACTIVE" && (
        m.missionStatus === "AT_PICKUP" && !m.pickupVerified ||
        m.missionStatus === "AT_DELIVERY" && !m.deliveryVerified);
}

export default function MissionOperationsPanel({ missions, selectedRobot, onSelectRobot, fresh, error, onSaved, visible }: {
    missions: TrackingMission[]; selectedRobot: string; onSelectRobot: (code: string) => void;
    fresh: boolean; error: string; onSaved: () => Promise<void>; visible: boolean;
}) {
    const { user, role } = useAuth();
    const { catalog } = useCatalog(visible);
    const canVerify = user?.roles.includes("Warehouse Operator") ?? false;
    const active = missions.filter(m => m.assignmentStatus === "ACTIVE")
        .sort((a, b) => Number(needsTransfer(b)) - Number(needsTransfer(a)) || b.missionId - a.missionId);
    const current = selectedRobot ? active.find(m => m.robotCode === selectedRobot) : active[0];
    const station = (id: number, type: string) => {
        const s = catalog?.stations.find(s => s.arucoId === id && (s.type === type || s.type === "TRANSFER"));
        return s?.name ?? `Transfer point QR${id}`;
    };
    const verificationPoint = (id: number) => {
        const s = catalog?.stations.find(s => s.arucoId === id);
        return s ? { name: s.name } : undefined;
    };
    return <aside className="operations-panel sf-card" aria-label="Mission operations">
        <div className="operations-panel-heading">
            <div><p className="sf-eyebrow">OPERATOR WORKSPACE</p><h2>Mission &amp; handover</h2>{current && <p className="text-xs sf-muted mt-1">{current.robotCode} · Task #{current.taskId} · Mission M{current.missionId}</p>}</div>
            <span className="sf-count">{active.length}</span>
        </div>
        <div className="operations-panel-body">
            {!current && <p className="sf-muted text-xs mb-4">Track the robot, check the goods, then confirm the transfer here.</p>}
            {error && <p role="alert" className="sf-notice sf-notice-danger">{error}</p>}
            {!fresh && <p role="status" className="sf-notice">Refreshing mission state. Transfer confirmation is locked until a current snapshot is available.</p>}
            {active.length > 1 && <label className="sf-label mb-4">Active mission
                <select className="sf-input mt-1" aria-label="Active mission" value={current?.robotCode ?? ""} onChange={e => onSelectRobot(e.target.value)}>
                    {!current && <option value="">Select a robot</option>}
                    {active.map(m => <option key={m.missionId} value={m.robotCode}>{m.robotCode} · Task #{m.taskId}{needsTransfer(m) ? " · Transfer required" : ""}</option>)}
                </select>
            </label>}
            {current ? <section key={current.missionId}>
                <div className="flex items-center gap-2 justify-between mb-1"><h3 className="sf-muted text-xs font-medium">{current.missionStatus.replace(/_/g, " ")}</h3>
                    {needsTransfer(current) && <span className="sf-badge sf-badge-amber">Action required</span>}</div>
                <details className="mission-route-details mt-3" open={!needsTransfer(current)}>
                <summary className="text-xs sf-muted">QR{current.pickupArUcoId} → QR{current.deliveryArUcoId} <span className="float-right">Route details</span></summary>
                <div className="handover-route">
                    <div><span className="route-dot"/><div><p className="sf-eyebrow">PICKUP · QR{current.pickupArUcoId}</p><strong>{station(current.pickupArUcoId, "PICKUP")}</strong></div><span className="sf-muted text-xs">{current.pickupVerified ? "Verified" : "Awaiting"}</span></div>
                    <div><span className="route-dot route-dot-delivery"/><div><p className="sf-eyebrow">DELIVERY · QR{current.deliveryArUcoId}</p><strong>{station(current.deliveryArUcoId, "DELIVERY")}</strong></div><span className="sf-muted text-xs">{current.deliveryVerified ? "Verified" : "Awaiting"}</span></div>
                </div>
                <div className="mt-4 mb-3"><div className="flex justify-between sf-muted text-xs mb-2"><span>Camera-confirmed checkpoints</span><span>{Math.min(current.nextWaypointIndex, current.waypoints?.length ?? 0)} / {current.waypoints?.length ?? 0}</span></div>
                    <div className="flex gap-1" aria-label="Route progress">{current.waypoints?.map((w, i) => <span key={i} title={`QR${w.arucoId} · ${i < current.nextWaypointIndex ? "Reached" : "Upcoming"}`} className={`route-segment ${i < current.nextWaypointIndex ? "reached" : ""}`} />)}</div></div>
                </details>
                {current.held && <p role="status" className="sf-notice">Safety hold active. Resolve the robot state before continuing.</p>}
                <MissionVerificationPanel mission={{ ...current, stationPresenceFresh: current.stationPresenceFresh && fresh }} canVerify={canVerify} onSaved={onSaved} compact pickupPoint={verificationPoint(current.pickupArUcoId)} deliveryPoint={verificationPoint(current.deliveryArUcoId)} />
                {!needsTransfer(current) && <div className="sf-empty mt-4"><strong>{current.missionStatus === "FAILED" ? "Recovery required" : "Following mission progress"}</strong><p>{current.missionStatus === "FAILED" ? "The assignment remains held until authorized recovery. Use robot safety controls." : "The confirmation form appears when the robot reaches pickup or delivery."}</p></div>}
            </section> : <div className="sf-empty"><span className="sf-empty-icon" aria-hidden="true">↗</span><h3>{selectedRobot ? `${selectedRobot} has no active mission` : "Ready for the next mission"}</h3><p>{canVerify ? "Create and assign a transport task. Its route and handover steps will appear here." : "Assigned missions and transfer progress appear here. Warehouse Operators confirm goods transfers."}</p>
                {canVerify && <Link className="sf-button sf-button-primary mt-4 inline-flex" to="/dashboard/tasks">Go to tasks</Link>}</div>}
        </div>
        {role !== "maintenance" && <Link className="operations-panel-footer" to={role === "admin" ? "/admin/qr-missions" : "/dashboard/qr-missions"}>View mission &amp; verification history <span>↗</span></Link>}
    </aside>;
}
