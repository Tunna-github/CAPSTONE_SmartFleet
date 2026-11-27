import { useEffect, useRef, useState } from "react";
import { apiClient } from "../api/client";
import { createPortal } from "react-dom";
import { useAuth } from "../context/AuthContext";

type Profile = { key: string; name: string; isActive: boolean };
type Operation = { id: string; targetKey: string; state: string; message: string; backupReady: boolean };
type Status = { shared: boolean; activeKey: string; profiles: Profile[];
  selection?: { available: boolean; blockedReason?: string | null; operation?: Operation | null } };
const terminal = new Set(["SUCCEEDED", "FAILED", "FAILED_HOLD"]);
const stage: Record<string, string> = { REQUESTED: "Request accepted", CHECKING: "Checking safety", BACKING_UP: "Saving backup",
  STOPPING_ROBOTS: "Stopping robots", STOPPING_WORLD: "Stopping old world", SWITCHING_MAP: "Activating map",
  STARTING_WORLD: "Starting Webots", VERIFYING: "Verifying robot localization", SUCCEEDED: "Map ready",
  FAILED: "Map unchanged", FAILED_HOLD: "Recovery required" };

export default function SharedMapNotice({ visible, sceneId, onMapChanged }: { visible: boolean; sceneId?: string; onMapChanged?: () => void }) {
  const { role } = useAuth();
  const [data, setData] = useState<Status | null>(null);
  const [target, setTarget] = useState<Profile | null>(null);
  const [reason, setReason] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState("");
  const [unavailable, setUnavailable] = useState(false);
  const requestId = useRef("");
  const previousMap = useRef("");
  const dialog = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!target) return;
    const previous = document.activeElement as HTMLElement | null;
    const overflow = document.body.style.overflow; document.body.style.overflow = "hidden";
    dialog.current?.querySelector("input")?.focus();
    const keyboard = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !sending) setTarget(null);
      if (e.key !== "Tab") return;
      const items = dialog.current?.querySelectorAll<HTMLElement>("input:not(:disabled),button:not(:disabled)");
      if (!items?.length) return;
      if (e.shiftKey && document.activeElement === items[0]) { e.preventDefault(); items[items.length - 1].focus(); }
      else if (!e.shiftKey && document.activeElement === items[items.length - 1]) { e.preventDefault(); items[0].focus(); }
    };
    window.addEventListener("keydown", keyboard);
    return () => { window.removeEventListener("keydown", keyboard); document.body.style.overflow = overflow; previous?.focus(); };
  }, [target, sending]);
  useEffect(() => {
    if (!visible) return;
    let active = true, loading = false;
    const refresh = async () => {
      if (loading) return;
      loading = true;
      try { const r = await apiClient.get<Status>("/api/v1/map-profiles"); if (active) { setData(r.data); setUnavailable(false); } }
      catch { if (active) setUnavailable(true); }
      finally { loading = false; }
    };
    void refresh(); const timer = window.setInterval(() => void refresh(), 3000);
    return () => { active = false; window.clearInterval(timer); };
  }, [visible, sceneId]);
  useEffect(() => {
    if (!data?.activeKey) return;
    if (previousMap.current && previousMap.current !== data.activeKey) onMapChanged?.();
    previousMap.current = data.activeKey;
  }, [data?.activeKey, onMapChanged]);
  useEffect(() => {
    if (data?.selection?.operation?.id === requestId.current) { setTarget(null); setError(""); }
  }, [data?.selection?.operation?.id]);
  if (!data?.shared) return null;
  const operation = data.selection?.operation;
  const changing = !!operation && !terminal.has(operation.state);
  const blocked = unavailable ? "Waiting for the simulation server. Selection is temporarily unavailable." : data.selection?.blockedReason;
  const canSelect = role === "admin" && data.selection?.available && !blocked && !changing && !sending;
  async function submit() {
    if (!target || !confirmed || !reason.trim() || !canSelect) return;
    setSending(true); setError("");
    try {
      await apiClient.post("/api/v1/map-profiles/select", { requestId: requestId.current, targetKey: target.key,
        expectedActiveKey: data!.activeKey, reason: reason.trim(), confirmed: true });
      setTarget(null);
      const response = await apiClient.get<Status>("/api/v1/map-profiles"); setData(response.data);
    } catch (ex) {
      const e = ex as { status?: number; message?: string };
      setError(e.status ? e.message || "Map selection was rejected. Reload and check the fleet." :
        "The server response was interrupted. Check progress before retrying; the same request ID will be reused.");
    } finally { setSending(false); }
  }
  return <section className="sf-card p-4 mb-4" aria-label="Warehouse map selection">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><p className="sf-eyebrow">SIMULATION MAP</p><h2 className="text-base font-semibold">{data.profiles.find(p => p.isActive)?.name ?? data.activeKey}</h2>
        <p className="sf-muted text-sm mt-1">Each warehouse keeps its own tasks and stations. One warehouse world runs at a time for all users.</p></div>
      <div className="flex flex-wrap gap-2" aria-label="Saved map versions">{data.profiles.map(p => <button key={p.key} type="button"
        className={p.isActive ? "sf-button sf-button-primary" : "sf-button"} aria-pressed={p.isActive}
        disabled={p.isActive || !canSelect} onClick={() => { setTarget(p); setReason(""); setConfirmed(false); setError(""); requestId.current = crypto.randomUUID(); }}>
        {p.key === "map1" ? "Map 1" : "Map 2"}{p.isActive ? " · Active" : ""}</button>)}</div>
    </div>
    {role !== "admin" && <p className="sf-muted text-xs mt-2">An Administrator can change the shared simulation map.</p>}
    {role === "admin" && blocked && !changing && <p className="sf-notice mt-3" role="status">{blocked}</p>}
    {(changing || operation?.state === "FAILED_HOLD" || operation?.state === "FAILED" || operation?.state === "SUCCEEDED") && <div className="sf-notice mt-3" role="status" aria-live="polite">
      <strong>{stage[operation!.state] ?? "Changing map"}</strong><span className="block text-sm mt-1">{operation!.message}</span>
      {operation!.backupReady && <span className="block text-xs mt-1">Database backup verified and retained on the simulation host.</span>}
      {changing && <span className="block text-xs mt-1">Dispatch is locked during the change. {unavailable ? "The server is restarting; progress will resume automatically." : "You can keep this page open."}</span>}
    </div>}
    {target && createPortal(<div className="map-switch-backdrop"><div ref={dialog} className="sf-card w-full max-w-xl p-6" role="dialog" aria-modal="true" aria-labelledby="map-change-title">
      <h2 id="map-change-title" className="text-xl font-semibold">Switch to {target.key === "map1" ? "Map 1" : "Map 2"}?</h2>
      <p className="sf-muted mt-3">This changes the map for everyone and reloads the Webots world. The system saves a database backup and waits for robot stop acknowledgements first. Active missions block the change.</p>
      <label className="block mt-4">Reason<input className="sf-input mt-2 w-full" value={reason} maxLength={500} onChange={e => setReason(e.target.value)} placeholder="Why are you changing the simulation map?" /></label>
      <label className="flex items-start gap-2 mt-4 text-sm"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />I confirm that this is a shared simulation world change.</label>
      {(error || blocked) && <p className="sf-notice sf-notice-danger mt-3" role="alert">{error || blocked}</p>}
      <div className="flex justify-end gap-2 mt-5"><button className="sf-button" disabled={sending} onClick={() => setTarget(null)}>Keep current map</button>
        <button className="sf-button sf-button-primary" disabled={!canSelect || !confirmed || !reason.trim()} onClick={() => void submit()}>{sending ? "Submitting…" : "Back up & switch map"}</button></div>
    </div></div>, document.body)}
  </section>;
}
