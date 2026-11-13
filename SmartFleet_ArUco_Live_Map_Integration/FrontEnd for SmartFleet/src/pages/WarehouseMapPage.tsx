import { useCallback, useEffect, useMemo, useState } from "react";
import * as signalR from "@microsoft/signalr";
import { tokenStorage } from "../api/client";
import { useAuth } from "../context/AuthContext";
import {
    getWarehouseMap,
    listMapWarehouses,
    removeArucoMapping,
    saveArucoMapping,
    type ArucoMapMarker,
    type RobotPositionUpdatedEvent,
    type WarehouseMapSnapshot,
    type WarehouseOption,
} from "../api/modules/warehouseMap";

// This demo grid can be expanded. The backend supports rows A..Z and columns 1..40.
const GRID_ROWS = 8;
const GRID_COLS = 12;
const STALE_AFTER_SECONDS = 20;

type Cell = { row: number; col: number };

function cellCode(row: number, col: number): string {
    return `${String.fromCharCode(65 + row)}${col}`;
}

function errorText(error: unknown): string {
    if (error && typeof error === "object" && "message" in error) {
        return String(error.message);
    }
    return "Request failed.";
}

function formatAge(timestamp: string | null, now: number): string {
    if (!timestamp) return "Never detected";
    const seconds = Math.max(0, Math.floor((now - new Date(timestamp).getTime()) / 1000));
    if (!Number.isFinite(seconds)) return "Invalid scan time";
    if (seconds < 60) return `${seconds}s ago`;
    return `${Math.floor(seconds / 60)}m ${seconds % 60}s ago`;
}

export function WarehouseMapPage() {
    const { role } = useAuth();
    const isAdmin = role === "admin";
    const [warehouses, setWarehouses] = useState<WarehouseOption[]>([]);
    const [warehouseId, setWarehouseId] = useState<number | null>(null);
    const [snapshot, setSnapshot] = useState<WarehouseMapSnapshot | null>(null);
    const [selected, setSelected] = useState<Cell>({ row: 0, col: 1 });
    const [markerIdText, setMarkerIdText] = useState("1");
    const [connectionStatus, setConnectionStatus] = useState("CONNECTING");
    const [loading, setLoading] = useState(true);
    const [saving, setSaving] = useState(false);
    const [error, setError] = useState("");
    const [notice, setNotice] = useState("");
    const [now, setNow] = useState(Date.now());

    const reload = useCallback(async (id: number) => {
        const data = await getWarehouseMap(id);
        setSnapshot((current) => current?.warehouseId === id || current === null ? data : current);
    }, []);

    useEffect(() => {
        let cancelled = false;
        listMapWarehouses()
            .then((data) => {
                if (cancelled) return;
                setWarehouses(data);
                setWarehouseId((current) => current ?? (data[0]?.warehouseId ?? null));
                if (!data.length) setError("No active warehouse exists in the database.");
            })
            .catch((e: unknown) => { if (!cancelled) setError(errorText(e)); })
            .finally(() => { if (!cancelled) setLoading(false); });
        return () => { cancelled = true; };
    }, []);

    useEffect(() => {
        const timer = window.setInterval(() => setNow(Date.now()), 1000);
        return () => window.clearInterval(timer);
    }, []);

    useEffect(() => {
        if (warehouseId === null) return;
        let cancelled = false;
        setSnapshot(null);
        setLoading(true);
        getWarehouseMap(warehouseId)
            .then((data) => { if (!cancelled) { setSnapshot(data); setError(""); } })
            .catch((e: unknown) => { if (!cancelled) setError(errorText(e)); })
            .finally(() => { if (!cancelled) setLoading(false); });
        return () => { cancelled = true; };
    }, [warehouseId]);

    useEffect(() => {
        if (warehouseId === null) return;
        let cancelled = false;
        let retryTimer: ReturnType<typeof setTimeout> | null = null;
        const baseUrl = (import.meta.env.VITE_API_URL || "https://localhost:7166").replace(/\/+$/, "");
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(`${baseUrl}/hubs/robots`, {
                accessTokenFactory: () => tokenStorage.get() ?? "",
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Warning)
            .build();

        const refresh = async () => {
            try {
                const data = await getWarehouseMap(warehouseId);
                if (!cancelled) setSnapshot(data);
            } catch (e: unknown) {
                if (!cancelled) setError(errorText(e));
            }
        };

        connection.on("RobotPositionUpdated", (event: RobotPositionUpdatedEvent) => {
            if (cancelled || event.warehouseId !== warehouseId) return;
            setSnapshot((previous) => {
                if (!previous || previous.warehouseId !== warehouseId) return previous;
                if (!previous.robots.some((robot) => robot.robotId === event.robotId)) {
                    return previous;
                }
                return {
                    ...previous,
                    robots: previous.robots.map((robot) => robot.robotId !== event.robotId
                        ? robot
                        : {
                            ...robot,
                            arucoId: event.arucoId,
                            cellCode: event.cellCode,
                            gridRow: event.gridRow,
                            gridColumn: event.gridColumn,
                            seenAt: event.seenAt,
                        }),
                };
            });
        });

        connection.on("MapConfigurationUpdated", (event: { warehouseId: number }) => {
            if (!cancelled && event.warehouseId === warehouseId) void refresh();
        });
        connection.onreconnecting(() => { if (!cancelled) setConnectionStatus("RECONNECTING"); });
        connection.onreconnected(() => {
            if (cancelled) return;
            setConnectionStatus("CONNECTED");
            void refresh();
        });
        connection.onclose(() => {
            if (cancelled) return;
            setConnectionStatus("DISCONNECTED");
            if (retryTimer === null) retryTimer = setTimeout(() => { retryTimer = null; void start(); }, 5000);
        });

        async function start() {
            try {
                await connection.start();
                if (cancelled) {
                    await connection.stop();
                    return;
                }
                setConnectionStatus("CONNECTED");
                await refresh(); // Recover events between first GET and hub connect.
            } catch (e: unknown) {
                if (!cancelled) {
                    setConnectionStatus("DISCONNECTED");
                    setError(`SignalR could not connect: ${errorText(e)}`);
                    if (retryTimer === null) {
                        retryTimer = setTimeout(() => { retryTimer = null; void start(); }, 5000);
                    }
                }
            }
        }
        setConnectionStatus("CONNECTING");
        void start();
        return () => {
            cancelled = true;
            if (retryTimer !== null) clearTimeout(retryTimer);
            // Initial start can still be negotiating during React StrictMode cleanup.
            if (connection.state !== signalR.HubConnectionState.Disconnected) {
                void connection.stop().catch(() => undefined);
            }
        };
    }, [warehouseId]);

    const markerByCell = useMemo(() => new Map<string, ArucoMapMarker>(
        (snapshot?.markers ?? []).map((m) => [`${m.gridRow}:${m.gridColumn}`, m]),
    ), [snapshot?.markers]);

    const robotByCell = useMemo(() => {
        const result = new Map<string, WarehouseMapSnapshot["robots"]>();
        for (const robot of snapshot?.robots ?? []) {
            if (robot.gridRow === null || robot.gridColumn === null) continue;
            const key = `${robot.gridRow}:${robot.gridColumn}`;
            const group = result.get(key) ?? [];
            group.push(robot);
            result.set(key, group);
        }
        return result;
    }, [snapshot?.robots]);

    const saveMapping = async () => {
        if (!isAdmin || warehouseId === null) return;
        const markerId = Number(markerIdText.trim());
        if (!Number.isInteger(markerId) || markerId < 0 || markerId > 9999) {
            setError("Enter a valid numeric ArUco ID (0-9999).");
            return;
        }
        setError("");
        setNotice("");
        setSaving(true);
        try {
            await saveArucoMapping(warehouseId, markerId, selected.row, selected.col);
            await reload(warehouseId);
            setNotice(`Saved: ArUco ${markerId} -> ${cellCode(selected.row, selected.col)}`);
        } catch (e: unknown) {
            setError(errorText(e));
        } finally {
            setSaving(false);
        }
    };

    const removeMapping = async (arucoId: number) => {
        if (!isAdmin || warehouseId === null) return;
        setError("");
        setNotice("");
        setSaving(true);
        try {
            await removeArucoMapping(warehouseId, arucoId);
            await reload(warehouseId);
            setNotice(`Removed ArUco ${arucoId}`);
        } catch (e: unknown) {
            setError(errorText(e));
        } finally {
            setSaving(false);
        }
    };

    return (
        <div className="flex-1 min-h-0 overflow-y-auto p-5 md:p-7"
            style={{ background: "var(--background)", color: "var(--text-primary)" }}>
            <div className="flex flex-wrap justify-between items-start gap-4 mb-6">
                <div>
                    <h1 className="text-2xl font-bold">Live Warehouse Map</h1>
                    <p className="text-sm mt-1" style={{ color: "var(--text-faint)" }}>
                        Place fixed ArUco markers on grid cells, then watch robot marker detections live.
                    </p>
                </div>
                <div className="flex items-center gap-3">
                    <span className="text-xs rounded-lg px-3 py-2 border"
                        style={{ borderColor: "var(--border-subtle)", color: connectionStatus === "CONNECTED" ? "#22c55e" : "#f59e0b" }}>
                        SignalR: {connectionStatus}
                    </span>
                    <select value={warehouseId ?? ""}
                        onChange={(e) => setWarehouseId(Number(e.target.value))}
                        className="rounded-lg border p-2 text-sm"
                        style={{ color: "var(--text-primary)", background: "var(--surface-2)", borderColor: "var(--border-subtle)" }}>
                        {warehouses.map((w) => (
                            <option key={w.warehouseId} value={w.warehouseId}>
                                {w.warehouseCode} - {w.warehouseName}
                            </option>
                        ))}
                    </select>
                </div>
            </div>

            {error && <div role="alert" className="mb-4 p-3 rounded-lg text-sm border border-red-500 text-red-500">{error}</div>}
            {notice && <div role="status" className="mb-4 p-3 rounded-lg text-sm border border-green-500 text-green-500">{notice}</div>}
            {loading && <p className="text-sm mb-4">Loading warehouse map...</p>}

            <div className="grid xl:grid-cols-[minmax(0,1fr)_300px] gap-5">
                <section className="rounded-xl p-4 border min-w-0"
                    style={{ background: "var(--surface-1)", borderColor: "var(--border-subtle)" }}>
                    <div className="flex flex-wrap justify-between gap-2 mb-3">
                        <h2 className="font-semibold">Grid (A1 to H12)</h2>
                        <span className="text-xs" style={{ color: "var(--text-faint)" }}>
                            {isAdmin ? "Click a cell, enter marker ID, then Save" : "Read-only live view"}
                        </span>
                    </div>
                    <div className="overflow-x-auto pb-2">
                        <div className="grid gap-1 min-w-[650px]" style={{ gridTemplateColumns: `repeat(${GRID_COLS}, minmax(0, 1fr))` }}>
                            {Array.from({ length: GRID_ROWS * GRID_COLS }, (_, idx) => {
                                const row = Math.floor(idx / GRID_COLS);
                                const col = (idx % GRID_COLS) + 1;
                                const marker = markerByCell.get(`${row}:${col}`);
                                const robots = robotByCell.get(`${row}:${col}`) ?? [];
                                const isSelected = selected.row === row && selected.col === col;
                                return (
                                    <button key={`${row}-${col}`} type="button"
                                        onClick={() => { if (isAdmin) setSelected({ row, col }); }}
                                        title={`${cellCode(row, col)}${marker ? ` | ArUco ${marker.arucoId}` : ""}`}
                                        className="relative min-h-[71px] rounded-md border text-center p-1 transition-colors"
                                        style={{
                                            background: isSelected && isAdmin ? "rgba(6,182,212,0.14)" : "var(--surface-2)",
                                            borderColor: isSelected && isAdmin ? "#06b6d4" : "var(--border-subtle)",
                                            cursor: isAdmin ? "pointer" : "default",
                                        }}>
                                        <div className="text-[10px] text-left" style={{ color: "var(--text-faint)" }}>
                                            {cellCode(row, col)}
                                        </div>
                                        {marker && <div className="text-[10px] font-semibold" style={{ color: "#22d3ee" }}>
                                            ID {marker.arucoId}
                                        </div>}
                                        {robots.map((robot) => {
                                            const age = robot.seenAt ? now - new Date(robot.seenAt).getTime() : Infinity;
                                            const stale = age > STALE_AFTER_SECONDS * 1000;
                                            return <div key={robot.robotId} className="rounded px-1 my-0.5 text-[11px] font-bold truncate"
                                                title={`${robot.robotCode}: ${formatAge(robot.seenAt, now)}${stale ? " (last known)" : ""}`}
                                                style={{ color: stale ? "#94a3b8" : "#ffffff", background: stale ? "#475569" : "#0891b2" }}>
                                                {"\uD83E\uDD16"} {robot.robotCode}
                                            </div>;
                                        })}
                                    </button>
                                );
                            })}
                        </div>
                    </div>
                    <p className="mt-3 text-xs" style={{ color: "var(--text-faint)" }}>
                        The robot icon shows its last scanned marker, NOT continuous motion between markers.
                        A gray robot means its marker fix is older than {STALE_AFTER_SECONDS} seconds.
                    </p>
                </section>

                <aside className="space-y-5 min-w-0">
                    {isAdmin && <section className="p-4 rounded-xl border"
                        style={{ background: "var(--surface-1)", borderColor: "var(--border-subtle)" }}>
                        <h2 className="font-semibold mb-3">Configure marker</h2>
                        <label className="text-xs block mb-1" htmlFor="map-marker-id">ArUco ID</label>
                        <input id="map-marker-id" type="number" min={0} max={9999} value={markerIdText}
                            onChange={(e) => setMarkerIdText(e.target.value)}
                            className="w-full rounded-md p-2 mb-3 border"
                            style={{ background: "var(--surface-2)", color: "var(--text-primary)", borderColor: "var(--border-subtle)" }}/>
                        <p className="text-sm mb-3">Selected position: <strong>{cellCode(selected.row, selected.col)}</strong></p>
                        <button type="button" disabled={saving || warehouseId === null}
                            onClick={() => void saveMapping()}
                            className="w-full rounded-lg p-2 font-semibold disabled:opacity-50"
                            style={{ background: "#0891b2", color: "white" }}>
                            {saving ? "Saving..." : "Save ArUco mapping"}
                        </button>
                    </section>}

                    <section className="p-4 rounded-xl border"
                        style={{ background: "var(--surface-1)", borderColor: "var(--border-subtle)" }}>
                        <h2 className="font-semibold mb-3">Configured markers ({snapshot?.markers.length ?? 0})</h2>
                        <div className="space-y-2 max-h-48 overflow-y-auto">
                            {snapshot?.markers.length === 0 && <p className="text-xs">No markers configured yet.</p>}
                            {snapshot?.markers.map((marker) => <div key={marker.arucoId}
                                className="flex items-center justify-between gap-2 text-xs rounded-md p-2 border"
                                style={{ borderColor: "var(--border-subtle)" }}>
                                <span>ArUco <strong>{marker.arucoId}</strong> = <strong>{marker.cellCode}</strong></span>
                                {isAdmin && <button type="button" disabled={saving}
                                    className="text-red-500 disabled:opacity-50"
                                    onClick={() => void removeMapping(marker.arucoId)}>Remove</button>}
                            </div>)}
                        </div>
                    </section>

                    <section className="p-4 rounded-xl border"
                        style={{ background: "var(--surface-1)", borderColor: "var(--border-subtle)" }}>
                        <h2 className="font-semibold mb-3">Robot last marker</h2>
                        <div className="space-y-2 max-h-64 overflow-y-auto">
                            {snapshot?.robots.length === 0 && <p className="text-xs">No active robots in this warehouse.</p>}
                            {snapshot?.robots.map((robot) => {
                                const age = robot.seenAt ? now - new Date(robot.seenAt).getTime() : Infinity;
                                const stale = age > STALE_AFTER_SECONDS * 1000;
                                return <div key={robot.robotId} className="rounded-md border p-2 text-xs"
                                    style={{ borderColor: "var(--border-subtle)" }}>
                                    <div className="flex justify-between gap-2">
                                        <strong>{robot.robotCode}</strong>
                                        <span style={{ color: stale ? "#f59e0b" : "#22c55e" }}>
                                            {stale ? "LAST KNOWN" : "RECENT FIX"}
                                        </span>
                                    </div>
                                    <div className="mt-1" style={{ color: "var(--text-faint)" }}>
                                        {robot.cellCode ?? "Unmapped / no scan"}
                                        {robot.arucoId !== null ? ` (ID ${robot.arucoId})` : ""}
                                        {" - "}{formatAge(robot.seenAt, now)}
                                    </div>
                                </div>;
                            })}
                        </div>
                    </section>
                </aside>
            </div>
        </div>
    );
}
