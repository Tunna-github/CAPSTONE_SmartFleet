import { useEffect, useState } from "react";
import { useAuth } from "../context/AuthContext";
import { getInterventions, intervene, type InterventionRow } from "../api/modules/interventions";

export default function RobotSafetyPanel() {
  const { role } = useAuth();
  const [open, setOpen] = useState(false);
  const [rows, setRows] = useState<InterventionRow[]>([]);
  const [reason, setReason] = useState("");
  const [confirmations, setConfirmations] = useState<Record<number, { stopped: boolean; unloaded: boolean; returned: boolean }>>({});
  const confirm = (id: number, field: "stopped" | "unloaded" | "returned", value: boolean) =>
    setConfirmations(prev => ({ ...prev, [id]: { ...(prev[id] ?? { stopped: false, unloaded: false, returned: false }), [field]: value } }));
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const refresh = async () => {
    try { setRows(await getInterventions()); }
    catch (e) { setMessage((e as Error).message); }
  };
  useEffect(() => {
    if (!open) return;
    void refresh();
    const timer = setInterval(() => void refresh(), 3000);
    return () => clearInterval(timer);
  }, [open]);
  const act = async (row: InterventionRow, action: string) => {
    setBusy(true);
    try {
      const checked = confirmations[row.assignmentId];
      setMessage(await intervene(row.taskId, action,
        action === "pause" ? reason.trim() || "Emergency software stop requested by user" : reason,
        checked?.stopped, checked?.unloaded, checked?.returned, row.assignmentId));
      setConfirmations(prev => { const next = { ...prev }; delete next[row.assignmentId]; return next; });
      await refresh();
    } catch (e) { setMessage((e as Error).message); }
    finally { setBusy(false); }
  };
  return <div className="fixed right-4 top-3 z-[100] text-sm" style={{ color: "var(--text-primary)" }}>
    <button className="rounded-lg bg-red-700 text-white px-3 py-2 font-bold" onClick={() => setOpen(!open)}>
      {open ? "Close robot safety" : "Robot safety / Pause"}
    </button>
    {open && <section className="mt-2 p-4 rounded-xl border shadow-xl max-h-[80vh] overflow-auto w-[min(640px,92vw)]"
      style={{ background: "var(--surface-2)", borderColor: "var(--border-medium)" }}>
      <p className="font-bold">Robot safety and task cancellation</p>
      <p className="my-2 text-xs">Pause sends a software stop. Task and resource locks remain held. Check robot acknowledgement; use the physical E-Stop for immediate danger.</p>
      <label>Reason (required for cancellation)
        <input className="block w-full p-2 my-2 rounded border" style={{ background: "var(--surface-3)" }}
          value={reason} maxLength={400} onChange={e => setReason(e.target.value)} />
      </label>
      <p role="status" className="my-2 whitespace-pre-wrap">{message}</p>
      {rows.length === 0 && <p>No active robot assignments.</p>}
      {rows.map(row => <article key={row.taskId} className="border-t py-3 space-y-2">
        <p className="font-bold">{row.robotCode} · {row.taskTrackingCode}</p>
        <p>{row.taskStatus} / {row.missionStatus} / {row.connectionStatus}</p>
        <p>Original pickup: {row.pickupStationName || row.pickupStationId || "see transport task"}</p>
        {role === "maintenance" && <div className="space-y-2 mb-3">
          <label className="block"><input type="checkbox" checked={!!confirmations[row.assignmentId]?.stopped} onChange={e => confirm(row.assignmentId, "stopped", e.target.checked)} /> I physically confirmed the robot is stopped and safe.</label>
          <label className="block"><input type="checkbox" checked={!!confirmations[row.assignmentId]?.unloaded} onChange={e => confirm(row.assignmentId, "unloaded", e.target.checked)} /> I confirmed the load is removed and accounted for.</label>
          <label className="block"><input type="checkbox" checked={!!confirmations[row.assignmentId]?.returned} onChange={e => confirm(row.assignmentId, "returned", e.target.checked)} /> I confirmed the load is back at this task's original pickup station.</label>
        </div>}
        <p>{row.intervention?.state ?? "NORMAL"}{row.intervention?.paused ? " · PAUSED / stop requested" : ""}</p>
        {row.intervention && <p>{row.intervention.commandAcknowledged ? "Robot acknowledged control" : "Waiting for robot acknowledgement"}</p>}
        <div className="flex flex-wrap gap-2">
          <button className="bg-red-700 text-white rounded px-3 py-2" disabled={busy} onClick={() => void act(row, "pause")}>Pause / Stop</button>
          {role === "operator" && <>
            <button className="border rounded px-3 py-2" disabled={busy || !reason.trim()} onClick={() => void act(row, "cancel")}>Direct cancel → Home</button>
            <button className="border rounded px-3 py-2" disabled={busy || !reason.trim()} onClick={() => void act(row, "request-cancel")}>Request Maintenance cancel</button>
          </>}
          {role === "maintenance" && row.intervention?.state === "CANCEL_REQUESTED" &&
            <button className="border rounded px-3 py-2" disabled={busy || !reason.trim() || !confirmations[row.assignmentId]?.stopped || !confirmations[row.assignmentId]?.unloaded}
              onClick={() => void act(row, "force-cancel")}>Force cancel (keep Maintenance hold)</button>}
          {role === "maintenance" && (row.taskStatus === "FAILED" || row.missionStatus === "FAILED" || row.operationalStatus === "ERROR" || row.intervention?.state === "CANCEL_REQUESTED") &&
            <button className="border rounded px-3 py-2" disabled={busy || !reason.trim() || !confirmations[row.assignmentId]?.stopped || !confirmations[row.assignmentId]?.unloaded || !confirmations[row.assignmentId]?.returned}
              onClick={() => void act(row, "recover-requeue")}>Recover &amp; Requeue (wait 30s)</button>}
        </div>
      </article>)}
    </section>}
  </div>;
}
