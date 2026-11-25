import { useEffect, useState } from "react";
import { apiClient } from "../api/client";
import { getRobots, type RobotInfo } from "../api/modules/robots";
import LiveFleetMap from "../components/LiveFleetMap";

export default function SimulationPage() {
    const [status, setStatus] = useState<{ enabled: boolean; mapCompatible: boolean; sceneId: string } | null>(null);
    const [robots, setRobots] = useState<RobotInfo[]>([]);
    const [viewer, setViewer] = useState("");
    const [error, setError] = useState("");
    const [busy, setBusy] = useState(false);
    const [tab, setTab] = useState("3d");
    useEffect(() => {
        let disposed = false;
        const refresh = async () => {
            try {
                const [scene, fleet] = await Promise.all([apiClient.get("/api/v1/simulation"), getRobots()]);
                if (!disposed) { setStatus(scene.data); setRobots(fleet); }
            } catch { if (!disposed) setError("Cannot load simulation configuration."); }
        };
        void refresh(); const timer = window.setInterval(() => void refresh(), 10000);
        const message = (event: MessageEvent) => {
            if (event.origin === window.location.origin && event.data?.type === "smartfleet-simulation-error") setError(String(event.data.message));
        };
        window.addEventListener("message", message);
        return () => { disposed = true; window.clearInterval(timer); window.removeEventListener("message", message); };
    }, []);
    async function connect() {
        setBusy(true); setError(""); setViewer("");
        try {
            const { data } = await apiClient.post<{ viewerPath: string }>("/api/v1/simulation/session", {});
            if (data.viewerPath !== "/simulation-viewer.html") throw new Error("Unexpected simulation viewer path");
            setViewer(data.viewerPath + "?session=" + Date.now());
        } catch (ex) { setError((ex as { message?: string }).message || "Cannot connect to Webots."); }
        finally { setBusy(false); }
    }
    return <main className="flex-1 min-h-0 flex flex-col p-5 gap-3">
        <div className="flex items-center flex-wrap gap-3"><h1 className="text-xl font-bold">Simulation &amp; live fleet map</h1>
            <button onClick={() => setTab("3d")} className="rounded border px-3 py-1">Webots 3D</button>
            <button onClick={() => setTab("2d")} className="rounded border px-3 py-1">Fleet map 2D</button>
            {tab === "3d" && <button disabled={!status?.enabled || busy} onClick={connect} className="ml-auto bg-cyan-600 rounded px-4 py-2 text-white disabled:opacity-40">{busy ? "Connecting..." : viewer ? "Reconnect 3D" : "Connect 3D"}</button>}
        </div>
        <p className="text-xs opacity-70">Shared simulation on the server · Battery values are simulated · Task, Pause and verification commands use SmartFleet permissions.</p>
        {status && !status.enabled && <p role="status" className="text-amber-500">Webots streaming is not configured on this server. Start the simulation Compose stack to enable 3D.</p>}
        {status?.enabled && !status.mapCompatible && <p role="alert" className="text-red-500">Saved map and simulation geometry differ. Dispatch is blocked until they are recommissioned.</p>}
        {error && <p role="alert" className="text-red-500">{error}</p>}
        <div className="flex-1 min-h-[350px] rounded-xl overflow-hidden border border-[var(--border-medium)]">
            {tab === "2d" ? <LiveFleetMap robots={robots} /> : viewer ? <iframe key={viewer} src={viewer} title="Live Webots 3D simulation"
                className="w-full h-full border-0" allow="fullscreen" /> : <div className="p-6 text-sm opacity-70">Connect to view the server's running Webots world. No local Webots installation is required for viewing.</div>}
        </div>
    </main>;
}
