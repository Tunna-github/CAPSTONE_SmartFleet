import { useEffect, useRef, useState } from "react";
import ChargingPanel from "../components/ChargingPanel";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { getRobots, type RobotInfo } from "../api/modules/robots";
import LiveFleetMap from "../components/LiveFleetMap";
import MissionOperationsPanel, { needsTransfer } from "../components/MissionOperationsPanel";
import useMissionWorkspace from "../hooks/useMissionWorkspace";

export default function SimulationPage({ visible = true }: { visible?: boolean }) {
    const [status, setStatus] = useState<{ enabled: boolean; mapCompatible: boolean; streamReady?: boolean; sceneId: string } | null>(null);
    const [robots, setRobots] = useState<RobotInfo[]>([]);
    const [viewer, setViewer] = useState("");
    const [viewerReady, setViewerReady] = useState(false);
    const viewerFrame = useRef<HTMLIFrameElement>(null);
    const [error, setError] = useState("");
    const [fleetError, setFleetError] = useState("");
    const [busy, setBusy] = useState(false);
    const connecting = useRef(false);
    const reconnectTimer = useRef<number | undefined>(undefined);
    const reconnects = useRef({ count: 0, since: 0 });
    const [tab, setTab] = useState("3d");
    const [mapOpened, setMapOpened] = useState(false);
    const [selectedRobot, setSelectedRobot] = useState("");
    const { missions, fresh, error: missionError, reload } = useMissionWorkspace(visible);
    const active = missions.filter(m => m.assignmentStatus === "ACTIVE");
    const awaiting = active.filter(needsTransfer).length;
    useEffect(() => {
        let disposed = false;
        const refresh = async () => {
            try {
                const [scene, fleet] = await Promise.all([apiClient.get("/api/v1/simulation"), getRobots()]);
                if (!disposed) { setStatus(scene.data); setRobots(fleet); setFleetError(""); }
            } catch { if (!disposed) setFleetError("Fleet/configuration updates unavailable. Previously reported values may be stale."); }
        };
        if (!visible) return;
        void refresh(); const timer = window.setInterval(() => void refresh(), 10000);
        return () => { disposed = true; window.clearInterval(timer); };
    }, [visible]);
    useEffect(() => {
        const message = (event: MessageEvent) => {
            if (event.origin !== window.location.origin || event.source !== viewerFrame.current?.contentWindow) return;
            if (event.data?.type === "smartfleet-simulation-error") {
                setError(String(event.data.message)); setViewer(""); setViewerReady(false);
                if (event.data.recoverable === true) {
                    const now = Date.now();
                    if (now - reconnects.current.since > 60000) reconnects.current = { count: 0, since: now };
                    if (reconnects.current.count < 3) {
                        const attempt = ++reconnects.current.count;
                        setError("3D viewing connection interrupted. Reconnecting automatically; robot tasks and handover progress are preserved.");
                        window.clearTimeout(reconnectTimer.current);
                        reconnectTimer.current = window.setTimeout(() => void connect(true), attempt * 1000);
                    }
                }
            }
            if (event.data?.type === "smartfleet-simulation-ready") { setViewerReady(true); setError(""); }
        };
        window.addEventListener("message", message);
        return () => { window.removeEventListener("message", message); window.clearTimeout(reconnectTimer.current); };
    }, []);
    useEffect(() => {
        if (visible && tab === "3d" && viewer) {
            const frame = requestAnimationFrame(() => viewerFrame.current?.contentWindow?.postMessage({ type: "smartfleet-viewer-resize" }, window.location.origin));
            return () => cancelAnimationFrame(frame);
        }
    }, [visible, tab, viewer]);
    async function connect(automatic = false) {
        if (connecting.current) return;
        window.clearTimeout(reconnectTimer.current);
        if (!automatic) reconnects.current = { count: 0, since: Date.now() };
        connecting.current = true; setBusy(true); setError(""); setViewer(""); setViewerReady(false);
        try {
            const { data } = await apiClient.post<{ viewerPath: string }>("/api/v1/simulation/session", {});
            if (data.viewerPath !== "/simulation-viewer.html") throw new Error("Unexpected simulation viewer path");
            setViewer(data.viewerPath + "?session=" + Date.now());
        } catch (ex) { setError((ex as { message?: string }).message || "Cannot connect to Webots."); }
        finally { connecting.current = false; setBusy(false); }
    }
    return <main className="tracking-page">
        <header className="tracking-header">
            <div><p className="sf-eyebrow">FLEET OPERATIONS</p><h1>Live tracking &amp; handover</h1><p className="sf-muted text-sm mt-1">Follow each mission and confirm pickup or delivery in one workspace.</p></div>
            <Link className="sf-button shrink-0" to="/simulation" target="_blank" rel="noopener noreferrer">Open tracking in new tab ↗</Link>
        </header>
        <div className="tracking-summary" aria-label="Fleet overview">
            <span><i className={`status-dot ${fleetError ? "" : "online"}`} /><strong>{fleetError ? "—" : robots.filter(r => r.connectionStatus === "ONLINE").length} / {robots.length}</strong> robots online</span>
            <span><strong>{fresh ? active.length : "—"}</strong> active missions</span>
            <span className={awaiting && fresh ? "transfer-required" : ""}><strong>{fresh ? awaiting : "—"}</strong> transfers awaiting confirmation</span>
            <span className="ml-auto sf-muted text-xs">Shared server simulation · Battery values simulated</span>
        </div>
        {status && !status.enabled && <p role="status" className="sf-notice">Webots streaming is not configured on this server. Start the simulation Compose stack to enable 3D.</p>}
        {status?.enabled && !status.mapCompatible && <p role="alert" className="sf-notice sf-notice-danger">Saved map and simulation geometry differ. Dispatch is blocked until they are recommissioned.</p>}
        {status?.enabled && status.mapCompatible && status.streamReady === false && <p role="status" className="sf-notice">Webots has no fresh position reports. It may be starting or unresponsive. Reconnecting the viewer cannot restart the simulator; active tasks keep their safety holds.</p>}
        {fleetError && <p role="alert" className="sf-notice sf-notice-danger">{fleetError}</p>}
        <div className="tracking-workspace">
            <section className="tracking-stage sf-card" aria-label="Fleet tracking">
                <div className="tracking-toolbar">
                    <div className="sf-segmented" aria-label="Tracking view"><button aria-pressed={tab === "2d"} onClick={() => { setTab("2d"); setMapOpened(true); }}>Fleet map 2D</button><button aria-pressed={tab === "3d"} onClick={() => setTab("3d")}>Webots 3D</button></div>
                    <label className="tracking-robot-filter sf-label">Robot<select className="sf-input" aria-label="Tracking robot" value={selectedRobot} onChange={e => setSelectedRobot(e.target.value)}><option value="">All robots</option>{robots.map(r => <option key={r.robotCode} value={r.robotCode}>{r.robotCode}</option>)}</select></label>
                    {tab === "3d" && <button disabled={!status?.enabled || busy} onClick={() => void connect()} className="sf-button sf-button-primary">{busy ? "Connecting..." : viewer ? "Reconnect 3D" : "Connect 3D"}</button>}
                </div>
                {error && <p role="alert" className="sf-notice sf-notice-danger m-3">{error}</p>}
                <div className="tracking-viewport">
                    {mapOpened && <div className="tracking-view" style={{ display: tab === "2d" ? "flex" : "none" }}><LiveFleetMap robots={robots} selectedRobot={selectedRobot} onSelectRobot={setSelectedRobot} showRobotStatus={false} missionSnapshot={missions} /></div>}
                    <div className="tracking-view" style={{ display: tab === "3d" ? "flex" : "none" }}>
                        {viewer ? <iframe ref={viewerFrame} src={viewer} title="Live Webots 3D simulation" className="w-full h-full border-0" allow="fullscreen" /> : <div className="tracking-placeholder"><div className="tracking-placeholder-symbol" aria-hidden="true">⬡</div><h2>View the live Webots world</h2><p>Connect to the server simulation to watch the robots in 3D. Use the panel on the right to confirm goods transfers.</p><span className="sf-badge">No local Webots installation required</span></div>}
                    </div>
                </div>
                <div className="tracking-fleet-strip" aria-label="Live robot status">
                    {robots.length === 0 && <p className="sf-muted text-xs p-2">No registered robots reported.</p>}
                    {robots.map(robot => {
                        const mission = active.find(m => m.robotCode === robot.robotCode);
                        return <button key={robot.robotCode} className="fleet-robot-card" aria-pressed={selectedRobot === robot.robotCode} onClick={() => setSelectedRobot(selectedRobot === robot.robotCode ? "" : robot.robotCode)}>
                            <div className="flex items-center justify-between gap-2"><strong>{robot.robotCode}</strong><span className="sf-muted text-[10px]"><i className={`status-dot ${!fleetError && robot.connectionStatus === "ONLINE" ? "online" : ""}`} />{fleetError ? "STALE" : robot.connectionStatus}</span></div>
                            <div className="flex justify-between gap-2 mt-1 text-xs"><span className="sf-muted">{robot.operationalStatus}</span><span>Battery {robot.batteryPercent ?? "?"}%</span></div>
                            <div className="robot-battery-track" aria-hidden="true"><span style={{ width: `${Math.max(0, Math.min(100, robot.batteryPercent ?? 0))}%`, background: robot.batteryPercent != null && robot.batteryPercent < 20 ? "#d97706" : "var(--primary)" }} /></div>
                            <span className="sf-muted text-[10px] block">{mission ? `Task #${mission.taskId} · ${mission.missionStatus.replace(/_/g, " ")}` : "No active mission"}</span>
                        </button>;
                    })}
                </div>
                <div className="tracking-stage-footer"><span><i className={`status-dot ${viewerReady ? "online" : ""}`} />{viewerReady ? "3D connected" : viewer ? "3D connecting" : "3D disconnected"}</span><span>3D stays connected while navigating. Interrupted viewing sessions reconnect automatically while you remain signed in.</span></div>
            </section>
            <div className="min-w-0 overflow-auto"><MissionOperationsPanel missions={missions} selectedRobot={selectedRobot} onSelectRobot={setSelectedRobot} fresh={fresh} error={missionError} onSaved={reload} visible={visible} /><ChargingPanel visible={visible}/></div>
        </div>
    </main>;
}
