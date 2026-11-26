import { useRef, useState } from "react";
import { apiClient } from "../api/client";

export interface VerificationMission {
    missionId: number;
    missionStatus: string;
    assignmentStatus: string;
    pickupVerified: boolean;
    deliveryVerified: boolean;
    packageRequired: boolean;
    maxQrAttempts: number;
    pickupQrAttempts: number;
    deliveryQrAttempts: number;
    held: boolean;
    stationPresenceFresh: boolean;
    pickupArUcoId?: number;
    deliveryArUcoId?: number;
    pickupVerificationCode?: string;
    deliveryVerificationCode?: string;
    attempts: { id: number; stage: string; type: string; accepted: boolean; result: string; note?: string; userId: number; createdAt: string }[];
    verifications: { verificationStage: string; verificationType: string; verifiedAt: string; verifiedBy: number; note?: string }[];
}

interface VerificationPoint { name: string }

function VerificationForm({ mission, stage, onSaved, point }: { mission: VerificationMission; stage: string; onSaved: () => Promise<void>; point?: VerificationPoint }) {
    const [method, setMethod] = useState("QRCODE");
    const [qr, setQr] = useState("");
    const [packageCode, setPackageCode] = useState("");
    const [note, setNote] = useState("");
    const [confirmed, setConfirmed] = useState(false);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState("");
    const retry = useRef<{ fingerprint: string; requestId: string } | null>(null);
    const attempts = stage === "PICKUP" ? mission.pickupQrAttempts : mission.deliveryQrAttempts;
    const locked = method === "QRCODE" && attempts >= mission.maxQrAttempts;
    const verificationCode = stage === "PICKUP" ? mission.pickupVerificationCode : mission.deliveryVerificationCode;
    const disabled = busy || mission.held || !mission.stationPresenceFresh || locked || !confirmed ||
        (method === "MANUAL" ? !note.trim() : !verificationCode || !qr.trim() || mission.packageRequired && !packageCode.trim());
    async function submit(event: React.FormEvent) {
        event.preventDefault();
        if (disabled) return;
        setBusy(true); setError("");
        const body = { stage, type: method, scannedQr: method === "QRCODE" ? qr.trim() : null,
            scannedPackageCode: method === "QRCODE" && packageCode.trim() ? packageCode.trim() : null,
            physicalTransferConfirmed: confirmed, note: note.trim() || null };
        const fingerprint = JSON.stringify(body);
        if (retry.current?.fingerprint !== fingerprint) retry.current = { fingerprint, requestId: crypto.randomUUID() };
        try {
            await apiClient.post(`/api/v1/missions/${mission.missionId}/verifications`, { ...body, requestId: retry.current.requestId });
            retry.current = null;
            await onSaved();
        } catch (ex) {
            const failure = ex as { status?: number; message?: string };
            setError(failure.message || "Verification failed");
            // A definite mismatch is recorded. A lost response retains the ID so retry cannot duplicate a transfer.
            if (failure.status === 422) retry.current = null;
            await onSaved();
        } finally { setBusy(false); }
    }
    const fieldClass = "sf-input block w-full mt-1";
    return <form onSubmit={submit} className="sf-verification mt-4 rounded-xl border p-4" style={{ borderColor: "var(--border-medium)" }} aria-label={`${stage} verification`}>
        <h3 className="font-semibold">{stage === "PICKUP" ? "Confirm pickup" : "Confirm delivery"}{(stage === "PICKUP" ? mission.pickupArUcoId : mission.deliveryArUcoId) != null && <> · QR{stage === "PICKUP" ? mission.pickupArUcoId : mission.deliveryArUcoId}</>}</h3>
        {point && <p className="text-xs mt-1 sf-muted">{point.name}</p>}
        <p className="text-xs opacity-70 mt-1">Robot remains stopped until Warehouse Operator confirms the physical transfer.</p>
        {(!mission.stationPresenceFresh || mission.held) && <p role="status" className="text-amber-500 mt-2">Waiting for a fresh stopped-robot report, or a safety hold is active.</p>}
        <label className="block mt-3 text-sm">Verification method
            <select className={fieldClass} value={method} onChange={e => setMethod(e.target.value)} disabled={busy}>
                <option value="QRCODE">Scan station QR</option><option value="MANUAL">Manual confirmation with reason</option>
            </select>
        </label>
        {method === "QRCODE" ? <>
            <label className="block mt-3 text-sm">Station QR payload
                <input className={fieldClass} value={qr} onChange={e => setQr(e.target.value)} maxLength={150} autoComplete="off" disabled={busy} />
            </label>
            {verificationCode ? <p className="sf-notice mt-2" aria-label="Station verification code">Verification code saved for this mission: <strong className="font-mono">{verificationCode}</strong>
                {(["P1", "D1"].includes(verificationCode)) && <span className="block mt-1">This mission retains its original code. Use the code saved above; new assignments use the updated station configuration.</span>}
            </p> : <p role="status" className="sf-notice mt-2">Waiting for this mission's saved verification code. Reload mission data before scanning.</p>}
            <p className="text-xs opacity-70 mt-1">Scan this point's station QR label. Pickup/delivery is the task stage; navigation ArUco IDs are separate.</p>
            {mission.packageRequired && <label className="block mt-3 text-sm">Package code
                <input className={fieldClass} value={packageCode} onChange={e => setPackageCode(e.target.value)} maxLength={150} disabled={busy} />
            </label>}
            <p className="text-xs mt-2">Failed QR attempts: {attempts}/{mission.maxQrAttempts}</p>
            {locked && <p role="alert" className="text-amber-500">QR retry limit reached. Investigate before using manual confirmation or requesting Maintenance recovery.</p>}
        </> : <label className="block mt-3 text-sm">Manual verification reason
            <textarea className={fieldClass} value={note} onChange={e => setNote(e.target.value)} maxLength={500} disabled={busy} />
        </label>}
        <label className="flex gap-2 mt-3 text-sm"><input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} disabled={busy} />
            {stage === "PICKUP" ? "I confirm the correct goods are loaded onto this robot." : "I confirm the correct goods are unloaded at the delivery station."}
        </label>
        {error && <p role="alert" className="text-red-500 mt-2">{error}</p>}
        <button type="submit" disabled={disabled} className="sf-button sf-button-primary mt-4 w-full">{busy ? "Saving..." : `Confirm ${stage.toLowerCase()}`}</button>
    </form>;
}

export default function MissionVerificationPanel({ mission, canVerify, onSaved, compact = false, pickupPoint, deliveryPoint }: { mission: VerificationMission; canVerify: boolean; onSaved: () => Promise<void>; compact?: boolean; pickupPoint?: VerificationPoint; deliveryPoint?: VerificationPoint }) {
    const stage = mission.missionStatus === "AT_PICKUP" && !mission.pickupVerified ? "PICKUP" :
        mission.missionStatus === "AT_DELIVERY" && !mission.deliveryVerified ? "DELIVERY" : null;
    return <div className={compact ? "verification-compact" : ""}>
        {!compact && <p className="mt-3 text-sm">Pickup: {mission.pickupVerified ? "Verified" : "Not verified"} · Delivery: {mission.deliveryVerified ? "Verified" : "Not verified"}</p>}
        {stage && mission.assignmentStatus === "ACTIVE" && (canVerify
            ? <VerificationForm key={`${mission.missionId}-${stage}`} mission={mission} stage={stage} onSaved={onSaved} point={stage === "PICKUP" ? pickupPoint : deliveryPoint} />
            : <p className="mt-3 text-amber-500">Waiting for Warehouse Operator to verify {stage.toLowerCase()}.</p>)}
        {mission.verifications?.map(v => <p key={v.verificationStage} className="text-xs mt-2">{v.verificationStage} · {v.verificationType} · user #{v.verifiedBy} · {new Date(v.verifiedAt).toLocaleString()}{v.note ? ` · ${v.note}` : ""}</p>)}
        {mission.attempts?.length > 0 && <details className="mt-3 text-xs"><summary>Verification history ({mission.attempts.length})</summary>
            {mission.attempts.map(a => <p key={a.id} className="mt-1">{new Date(a.createdAt).toLocaleString()} · {a.stage} · {a.type} · {a.result} · user #{a.userId}{a.note ? ` · ${a.note}` : ""}</p>)}
        </details>}
    </div>;
}
