import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { useAuth } from "../context/AuthContext";
import { getInterventions, intervene, type InterventionRow } from "../api/modules/interventions";
import ChargingPanel from "./ChargingPanel";

const stages: Record<string, { title: string; detail: string }> = {
  CANCEL_STOP_PENDING: { title: "Cancelling safely", detail: "The empty robot is stopping at a camera-confirmed checkpoint. It will then return to a reserved charging bay." },
  RETURNING_HOME: { title: "Preparing return to charging", detail: "The robot is confirming its stopped position before the charging controller takes over. It remains unavailable for new tasks." },
  CANCELLED_HOLD: { title: "Cancelled · recovery required", detail: "A safe return position could not be confirmed. Maintenance must inspect the empty robot and obtain a fresh camera QR. The robot will not move blindly." },
  CANCEL_REQUESTED: { title: "Waiting for Maintenance", detail: "Movement is held. Maintenance must check the robot and account for any load before cancelling or recovering the task." },
};

export default function RobotSafetyPanel({ isCollapsed = false }: { isCollapsed?: boolean }) {
  const { role } = useAuth();
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<InterventionRow[]>([]);
  const [reason, setReason] = useState("");
  const [confirmations, setConfirmations] = useState<Record<number, { stopped: boolean; unloaded: boolean; returned: boolean }>>({});
  const [message, setMessage] = useState("");
  const [readError, setReadError] = useState("");
  const [busy, setBusy] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null), panel = useRef<HTMLElement>(null), close = useRef<HTMLButtonElement>(null);
  const confirm = (id: number, field: "stopped" | "unloaded" | "returned", value: boolean) =>
    setConfirmations(prev => ({ ...prev, [id]: { ...(prev[id] ?? { stopped: false, unloaded: false, returned: false }), [field]: value } }));
  async function refresh() {
    try { setRows(await getInterventions()); setReadError(""); }
    catch (e) { setReadError((e as Error).message); }
  }
  useEffect(() => {
    if (!open) return;
    void refresh(); const timer = window.setInterval(() => void refresh(), 3000);
    const overflow = document.body.style.overflow; document.body.style.overflow = "hidden"; close.current?.focus();
    const key = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
      if (e.key !== "Tab") return;
      const controls = Array.from(panel.current?.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), summary, select:not(:disabled), a[href]') ?? []).filter(el => el.getClientRects().length > 0);
      const first = controls[0], last = controls.at(-1);
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last?.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first?.focus(); }
    };
    window.addEventListener("keydown", key);
    return () => { window.clearInterval(timer); window.removeEventListener("keydown", key); document.body.style.overflow = overflow; trigger.current?.focus(); };
  }, [open]);
  async function act(row: InterventionRow, action: string) {
    setBusy(true); setMessage("");
    try {
      const checked = confirmations[row.assignmentId];
      setMessage(await intervene(row.taskId, action, action === "pause" ? reason.trim() || "Emergency software stop requested by user" : reason,
        checked?.stopped, checked?.unloaded, checked?.returned, row.assignmentId));
      setConfirmations(prev => { const next = { ...prev }; delete next[row.assignmentId]; return next; }); await refresh();
    } catch (e) { setMessage((e as Error).message); }
    finally { setBusy(false); }
  }
  const drawer = open && <>
    <div className="safety-backdrop" onClick={() => setOpen(false)} aria-hidden="true" />
    <section ref={panel} id="robot-safety-panel" role="dialog" aria-modal="true" aria-label="Robot safety controls" className="safety-drawer">
      <header className="safety-header"><div><p className="sf-eyebrow">FLEET SAFETY</p><h2>Stop, cancel &amp; recover</h2></div>
        <button ref={close} type="button" aria-label="Close safety panel" className="sf-button !px-3" onClick={() => setOpen(false)}>✕</button></header>
      <div className="safety-content">
        <p className="safety-intro"><strong>Pause stops movement.</strong> Cancel ends the task and returns an empty, healthy robot to charging. A loaded or faulty robot needs Maintenance.</p>
        <p className="sf-muted text-xs">Software Pause is not a physical E-Stop. Use the device E-Stop for immediate danger.</p>
        {readError && <p role="alert" className="sf-notice sf-notice-danger mt-3">Status could not refresh: {readError}. Cancellation controls are locked.</p>}
        {message && <p role="status" className="sf-notice mt-3 whitespace-pre-wrap">{message}</p>}
        <label className="sf-label mt-4">Reason <span className="sf-muted text-xs font-normal">Required for cancellation or recovery</span>
          <input className="sf-input mt-1" placeholder="For example: duplicate order or wrong item requested" value={reason} maxLength={400} onChange={e => setReason(e.target.value)} /></label>
        {rows.length === 0 && <div className="safety-empty"><strong>No transport tasks need intervention</strong><p className="sf-muted text-sm mt-1">Charging and return controls appear below when needed.</p></div>}
        {rows.map(row => {
          const state = row.intervention?.state ?? "NORMAL", cancelled = row.taskStatus === "CANCELLED";
          const recovered = row.taskStatus === "QUEUED" && row.assignmentStatus === "FAILED";
          const pending = ["CANCEL_STOP_PENDING", "RETURNING_HOME"].includes(state), paused = !!row.intervention?.paused;
          const canDirect = !cancelled && !recovered && state === "NORMAL" && !paused && !row.pickupVerified && ["INITIALIZING", "NAVIGATING_TO_PICKUP"].includes(row.missionStatus) && row.connectionStatus === "ONLINE" && row.operationalStatus === "BUSY";
          const info = recovered ? { title: "Task queued · robot awaiting recovery", detail: "The failed attempt is preserved. Maintenance can return the inspected empty robot to charging before it takes another task." } : paused && pending ? {title:"Cancellation return paused",detail:"Movement has stopped. Request Maintenance recovery before continuing the return; task and resource locks remain held."} : stages[state] ?? (paused ? { title: "Movement paused", detail: "The task and resource locks remain held. Maintenance recovery is required before movement resumes." } : { title: "Task in progress", detail: canDirect ? "No pickup confirmed. Direct cancellation is available while the device is healthy." : "Load or device state needs Maintenance review before cancellation." });
          const checked = confirmations[row.assignmentId], physical = !!checked?.stopped && !!checked?.unloaded && !!reason.trim() && !busy && !readError;
          return <article key={row.assignmentId} className="safety-task">
            <div className="flex justify-between gap-3 items-start"><div><h3>{row.robotCode}</h3><p className="sf-muted text-xs">Task #{row.taskId} · Pickup: {row.pickupStationName || "See transport task"}</p></div><span className="sf-badge">{row.connectionStatus}</span></div>
            <div className={`safety-stage ${state === "CANCELLED_HOLD" || paused ? "safety-stage-warning" : ""}`}><strong>{info.title}</strong><p>{info.detail}</p>{row.intervention && <span className="text-xs sf-muted block mt-2">{row.intervention.commandAcknowledged ? "Robot acknowledged control" : "Waiting for robot acknowledgement"}</span>}</div>
            {!recovered && <div className="safety-action"><div><strong>Pause movement</strong><p>Stop now and keep the task, load and resource locks.</p></div><button className="sf-button safety-stop" disabled={busy} onClick={() => void act(row, "pause")}>Pause / Stop</button></div>}
            {role === "operator" && !recovered && <>
              {!cancelled && <div className="safety-action"><div><strong>Cancel &amp; return to charging</strong><p>{canDirect ? "Finish the current empty leg at a safe QR, then reserve a charging bay." : "Unavailable while paused, loaded, offline or awaiting another intervention. Use Maintenance recovery."}</p></div><button className="sf-button sf-button-primary" disabled={busy || !!readError || !reason.trim() || !canDirect} onClick={() => void act(row, "cancel")}>Cancel task &amp; return</button></div>}
              {(!pending || paused) && state !== "CANCEL_REQUESTED" && <div className="safety-action"><div><strong>Ask Maintenance</strong><p>Use for a load, device fault or a cancelled robot without a safe QR.</p></div><button className="sf-button" disabled={busy || !!readError || !reason.trim()} onClick={() => void act(row, "request-cancel")}>Request Maintenance cancel</button></div>}
            </>}
            {role === "maintenance" && !pending && <div className="safety-maintenance"><h4>Physical recovery checklist</h4>
              <label><input type="checkbox" checked={!!checked?.stopped} onChange={e => confirm(row.assignmentId, "stopped", e.target.checked)} /> I physically confirmed the robot is stopped and safe.</label>
              <label><input type="checkbox" checked={!!checked?.unloaded} onChange={e => confirm(row.assignmentId, "unloaded", e.target.checked)} /> I confirmed the load is removed and accounted for.</label>
              {!cancelled && !recovered && <label><input type="checkbox" checked={!!checked?.returned} onChange={e => confirm(row.assignmentId, "returned", e.target.checked)} /> I confirmed the load is back at this task's original pickup station.</label>}
              {state === "CANCEL_REQUESTED" && <button className="sf-button" disabled={!physical} onClick={() => void act(row, "force-cancel")}>Force cancel (keep Maintenance hold)</button>}
              {(cancelled && state === "CANCELLED_HOLD" || recovered) && <button className="sf-button sf-button-primary" disabled={!physical || !row.intervention?.commandAcknowledged} onClick={() => void act(row, "return-to-charge")}>Return empty robot to charging</button>}
              {!cancelled && !recovered && (row.taskStatus === "FAILED" || row.missionStatus === "FAILED" || row.operationalStatus === "ERROR" || state === "CANCEL_REQUESTED") && <button className="sf-button" disabled={!physical || !checked?.returned} onClick={() => void act(row, "recover-requeue")}>Recover &amp; Requeue (wait 30s)</button>}
            </div>}
            <details className="text-xs sf-muted mt-3"><summary>Technical details</summary><p className="break-all mt-2">{row.taskTrackingCode}</p><p>{row.taskStatus} · {row.missionStatus} · {state}</p></details>
          </article>;
        })}
        {rows.length === 0 ? <ChargingPanel visible={open} /> : <details className="safety-charging"><summary>Charging and return controls</summary><ChargingPanel visible={open} /></details>}
      </div>
    </section>
  </>;
  return <>
    <button ref={trigger} type="button" className="flex w-full items-center justify-center gap-2 rounded-lg bg-red-700 text-white px-2 py-3 text-xs font-bold hover:bg-red-800 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-red-500" aria-label={open ? "Close robot safety" : "Robot safety / Pause"} title="Robot safety / Pause" aria-expanded={open} aria-controls="robot-safety-panel" onClick={() => setOpen(!open)}>
      <svg aria-hidden="true" width="16" height="16" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16" rx="1" /><rect x="14" y="4" width="4" height="16" rx="1" /></svg>{!isCollapsed && <span>Robot safety / Pause</span>}
    </button>
    {drawer && createPortal(drawer, document.body)}
  </>;
}
