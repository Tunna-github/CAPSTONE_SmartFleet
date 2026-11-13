import { useState, useCallback, useEffect, useMemo } from "react";
 
import { useData } from "../context/DataContext";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn } from "../components/SharedUI";
import { WarehouseGrid, createDefaultWarehouseLayout, type WarehouseLayout } from "../components/WarehouseGrid";

// ─── TYPES & INTERFACES ──────────────────────────────────────────────────────
type AdminTab = "robots" | "warehouse";

interface RobotAsset {
    id: string; model: string; mac: string; ip: string;
    dateAdded: string; firmware: string; maintenance: boolean; mqtt: "ONLINE" | "OFFLINE";
}

// ─── SEED DATA ───────────────────────────────────────────────────────────────
const SEED_ROBOT_ASSETS: RobotAsset[] = [
    { id: "AMR-001", model: "FleetBot X200", mac: "A4:C3:F0:85:AC:01", ip: "192.168.1.101", dateAdded: "2025-03-12", firmware: "v3.2.1", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-002", model: "FleetBot X200", mac: "A4:C3:F0:85:AC:02", ip: "192.168.1.102", dateAdded: "2025-03-12", firmware: "v3.2.1", maintenance: false, mqtt: "ONLINE" },
];

const MODEL_OPTIONS = ["FleetBot X100", "FleetBot X200", "FleetBot X300", "FleetBot X400 (Beta)"];

// ─── MAIN COMPONENT ──────────────────────────────────────────────────────────
export function AdminDashboard() {
    const { robots: ctxRobots, toggleMaintenance } = useData();
    const [tab, setTab] = useState<AdminTab>("robots");
    const [search, setSearch] = useState("");
    const [toast, setToast] = useState<{ msg: string; type: "success" | "error" | "info" } | null>(null);

    const [robots, setRobots] = useState<RobotAsset[]>(SEED_ROBOT_ASSETS);
    const [addRobotOpen, setAddRobotOpen] = useState(false);
    
    // Form thêm Robot
    const [newRId, setNewRId] = useState("");
    const [newRModel, setNewRModel] = useState(MODEL_OPTIONS[1]);
    const [newRMac, setNewRMac] = useState("");
    const [newRIp, setNewRIp] = useState("");

    const [warehouseLayout, setWarehouseLayout] = useState<WarehouseLayout>(() => createDefaultWarehouseLayout());
    const [warehouseScale, setWarehouseScale] = useState(1);
    const [links, setLinks] = useState<Array<[string, string]>>(() => {
        const layout = createDefaultWarehouseLayout();
        const stations: Array<{ label: string; r: number; c: number }> = [];
        for (let r = 0; r < layout.length; r++) {
            for (let c = 0; c < layout[r]!.length; c++) {
                const cell = layout[r]![c]!;
                if (cell.type === "station" && cell.label) stations.push({ label: cell.label, r, c });
            }
        }
        const edges: Array<[string, string]> = [];
        const byRow = new Map<number, Array<{ label: string; c: number }>>();
        const byCol = new Map<number, Array<{ label: string; r: number }>>();
        for (const s of stations) {
            const row = byRow.get(s.r) ?? [];
            row.push({ label: s.label, c: s.c });
            byRow.set(s.r, row);
            const col = byCol.get(s.c) ?? [];
            col.push({ label: s.label, r: s.r });
            byCol.set(s.c, col);
        }
        for (const row of byRow.values()) {
            row.sort((a, b) => a.c - b.c);
            for (let i = 0; i < row.length - 1; i++) edges.push([row[i]!.label, row[i + 1]!.label]);
        }
        for (const col of byCol.values()) {
            col.sort((a, b) => a.r - b.r);
            for (let i = 0; i < col.length - 1; i++) edges.push([col[i]!.label, col[i + 1]!.label]);
        }
        return edges;
    });
    const [pendingStation, setPendingStation] = useState<string | null>(null);

    // --- Handlers: Robot ---
    const saveNewRobot = () => {
        if (!newRId.trim() || !newRMac.trim()) { setToast({ msg: "Robot ID and MAC are required.", type: "error" }); return; }
        setRobots((prev) => [...prev, {
            id: newRId.trim(), model: newRModel, mac: newRMac.trim(), ip: newRIp.trim() || "0.0.0.0",
            dateAdded: new Date().toISOString().slice(0, 10), firmware: "v4.0.0", maintenance: false, mqtt: "OFFLINE",
        }]);
        setAddRobotOpen(false); setNewRId(""); setNewRMac(""); setNewRIp("");
    };

    const handleToggleMaintenance = (id: string) => {
        setRobots((prev) => prev.map((r) => r.id === id ? { ...r, maintenance: !r.maintenance, mqtt: !r.maintenance ? "OFFLINE" : "ONLINE" } : r));
        toggleMaintenance(id);
    };

    const filteredRobots = robots.filter((r) => !search || r.id.toLowerCase().includes(search.toLowerCase()) || r.mac.toLowerCase().includes(search.toLowerCase()));

    const demoGridRobots = useMemo(() => {
        const stationPoints = [
            { x: 9, y: 6 }, { x: 13, y: 6 }, { x: 17, y: 6 },
            { x: 9, y: 9 }, { x: 13, y: 9 }, { x: 17, y: 9 },
            { x: 9, y: 12 }, { x: 13, y: 12 }, { x: 17, y: 12 },
        ];
        return ctxRobots.map((rb, idx) => {
            const from = stationPoints[idx % stationPoints.length]!;
            const to = stationPoints[(idx + 3) % stationPoints.length]!;
            return {
            id: rb.id,
            x: from.x,
            y: from.y,
            tx: to.x,
            ty: to.y,
            battery: rb.battery,
            mqtt: rb.mqtt,
            missionId: rb.missionId,
            color: rb.mqtt === "ONLINE" ? "#22d3ee" : "#ef4444",
            };
        });
    }, [ctxRobots]);

    const stationLabels = useMemo(() => {
        const out: string[] = [];
        for (let r = 0; r < warehouseLayout.length; r++) {
            for (let c = 0; c < warehouseLayout[r]!.length; c++) {
                const cell = warehouseLayout[r]![c]!;
                if (cell.type === "station" && cell.label) out.push(cell.label);
            }
        }
        out.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
        return out;
    }, [warehouseLayout]);

    const stationPos = useMemo(() => {
        const map = new Map<string, { r: number; c: number }>();
        for (let r = 0; r < warehouseLayout.length; r++) {
            for (let c = 0; c < warehouseLayout[r]!.length; c++) {
                const cell = warehouseLayout[r]![c]!;
                if (cell.type === "station" && cell.label) map.set(cell.label, { r, c });
            }
        }
        return map;
    }, [warehouseLayout]);

    const computeDefaultLinks = useCallback((layout: WarehouseLayout): Array<[string, string]> => {
        const stations: Array<{ label: string; r: number; c: number }> = [];
        for (let r = 0; r < layout.length; r++) {
            for (let c = 0; c < layout[r]!.length; c++) {
                const cell = layout[r]![c]!;
                if (cell.type === "station" && cell.label) stations.push({ label: cell.label, r, c });
            }
        }
        const edges: Array<[string, string]> = [];
        const byRow = new Map<number, Array<{ label: string; c: number }>>();
        const byCol = new Map<number, Array<{ label: string; r: number }>>();
        for (const s of stations) {
            const row = byRow.get(s.r) ?? [];
            row.push({ label: s.label, c: s.c });
            byRow.set(s.r, row);
            const col = byCol.get(s.c) ?? [];
            col.push({ label: s.label, r: s.r });
            byCol.set(s.c, col);
        }
        for (const row of byRow.values()) {
            row.sort((a, b) => a.c - b.c);
            for (let i = 0; i < row.length - 1; i++) edges.push([row[i]!.label, row[i + 1]!.label]);
        }
        for (const col of byCol.values()) {
            col.sort((a, b) => a.r - b.r);
            for (let i = 0; i < col.length - 1; i++) edges.push([col[i]!.label, col[i + 1]!.label]);
        }
        return edges;
    }, []);

    const normalizeLink = useCallback((a: string, b: string): [string, string] => {
        return a.localeCompare(b, undefined, { numeric: true }) <= 0 ? [a, b] : [b, a];
    }, []);

    const toggleLink = useCallback((a: string, b: string) => {
        const [x, y] = normalizeLink(a, b);
        const key = `${x}::${y}`;
        setLinks((prev) => {
            const exists = prev.some(([p, q]) => `${p}::${q}` === key);
            if (exists) return prev.filter(([p, q]) => `${p}::${q}` !== key);
            return [...prev, [x, y]];
        });
    }, [normalizeLink]);

    const handleStationClick = useCallback((label: string) => {
        if (!pendingStation) {
            setPendingStation(label);
            return;
        }
        if (pendingStation === label) {
            setPendingStation(null);
            return;
        }
        const a = stationPos.get(pendingStation);
        const b = stationPos.get(label);
        if (!a || !b || (a.r !== b.r && a.c !== b.c)) {
            setToast({ msg: "Chỉ hỗ trợ nối 2 điểm cùng hàng hoặc cùng cột.", type: "error" });
            setPendingStation(null);
            return;
        }
        toggleLink(pendingStation, label);
        setPendingStation(null);
    }, [pendingStation, stationPos, toggleLink]);

    const zoomOut = useCallback(() => setWarehouseScale((s) => Math.max(0.5, Math.round((s - 0.1) * 10) / 10)), []);
    const zoomIn = useCallback(() => setWarehouseScale((s) => Math.min(2.5, Math.round((s + 0.1) * 10) / 10)), []);
    const zoomReset = useCallback(() => setWarehouseScale(1), []);

    return (
        <div className="flex flex-col h-full overflow-hidden transition-colors" style={{ background: "var(--background)" }}>
            <PageHeader title="Admin Dashboard" sub="Quản lý Robot & Bản đồ Sa bàn">
                {tab === "robots" && <PrimaryBtn onClick={() => setAddRobotOpen(true)}><Icon d={IC.plus} size={15} /> Thêm Robot</PrimaryBtn>}
                {tab === "warehouse" && <PrimaryBtn onClick={() => {
                    const next = createDefaultWarehouseLayout();
                    setWarehouseLayout(next);
                    setLinks(computeDefaultLinks(next));
                    setPendingStation(null);
                    setWarehouseScale(1);
                }}><Icon d={IC.refresh} size={15} /> Reset Map</PrimaryBtn>}
            </PageHeader>

            {/* Tabs */}
            <div className="shrink-0 flex px-6" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                {([["robots", "Robot Assets", IC.robots], ["warehouse", "Warehouse Map", IC.map]] as const).map(([id, label, icon]) => {
                    const act = tab === id;
                    return (
                        <button
                            key={id}
                            onClick={() => { setTab(id as AdminTab); setSearch(""); }}
                            className="flex items-center gap-2 px-5 py-3.5 text-[13px] font-semibold transition-all"
                            style={{ color: act ? "#22d3ee" : "var(--text-faint)", background: "transparent", border: "none", borderBottom: act ? "2px solid #22d3ee" : "2px solid transparent", marginBottom: -1 }}
                        >
                            <Icon d={icon} size={14} /> {label}
                        </button>
                    );
                })}
            </div>

            {/* View Area */}
            <div className="flex-1 overflow-hidden px-6 pb-6 pt-4">
                <div className="h-full rounded-2xl overflow-hidden flex flex-col transition-colors" style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}>
                    
                    {tab === "robots" && (
                        <div className="overflow-auto flex-1">
                            <RobotTable robots={filteredRobots} onToggle={handleToggleMaintenance} />
                        </div>
                    )}

                    {tab === "warehouse" && (
                        <div className="flex flex-col h-full">
                            <div className="shrink-0 flex flex-wrap items-center gap-3 px-4 py-3" style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                                <span className="text-[11px] font-mono" style={{ color: "var(--text-faint)" }}>
                                    Click 2 stations to toggle a bidirectional link. Selected: {pendingStation ?? "—"}
                                </span>
                                <div className="ml-auto flex items-center gap-2">
                                    <button type="button" onClick={zoomOut} className="px-3 py-2 rounded-lg border text-[12px]" style={{ background: "var(--surface-3)", borderColor: "var(--border-medium)", color: "var(--text-primary)" }}>−</button>
                                    <button type="button" onClick={zoomReset} className="px-3 py-2 rounded-lg border text-[12px]" style={{ background: "var(--surface-3)", borderColor: "var(--border-medium)", color: "var(--text-primary)" }}>{Math.round(warehouseScale * 100)}%</button>
                                    <button type="button" onClick={zoomIn} className="px-3 py-2 rounded-lg border text-[12px]" style={{ background: "var(--surface-3)", borderColor: "var(--border-medium)", color: "var(--text-primary)" }}>+</button>
                                </div>
                            </div>
                            <div className="flex-1 overflow-hidden" style={{ background: "var(--surface-3)", minHeight: 520 }}>
                                <WarehouseGrid robots={demoGridRobots} grid={warehouseLayout} links={links} scale={warehouseScale} onStationClick={handleStationClick} />
                            </div>
                            <div className="shrink-0 px-4 py-3" style={{ borderTop: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                                <div className="flex flex-wrap items-center gap-3">
                                    <button
                                        type="button"
                                        onClick={() => { setLinks([]); setPendingStation(null); }}
                                        className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                                        style={{ background: "rgba(239,68,68,0.10)", borderColor: "rgba(239,68,68,0.25)", color: "#f87171" }}
                                    >
                                        Clear Links
                                    </button>
                                    <button
                                        type="button"
                                        onClick={() => { setLinks(computeDefaultLinks(warehouseLayout)); setPendingStation(null); }}
                                        className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                                        style={{ background: "var(--surface-3)", borderColor: "var(--border-medium)", color: "var(--text-primary)" }}
                                    >
                                        Reset Links
                                    </button>
                                    <span className="ml-auto text-[11px] font-mono" style={{ color: "var(--text-dimmest)" }}>
                                        Links: {links.length} · Stations: {stationLabels.length}
                                    </span>
                                </div>
                                {links.length > 0 && (
                                    <div className="mt-3 flex flex-wrap gap-2">
                                        {links.map(([a, b]) => {
                                            const key = `${a}::${b}`;
                                            return (
                                                <button
                                                    key={key}
                                                    type="button"
                                                    onClick={() => toggleLink(a, b)}
                                                    className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold border transition-colors"
                                                    style={{ background: "rgba(6,182,212,0.10)", borderColor: "rgba(6,182,212,0.25)", color: "#22d3ee" }}
                                                    aria-label={`Remove link ${a} to ${b}`}
                                                >
                                                    {a} ↔ {b}
                                                </button>
                                            );
                                        })}
                                    </div>
                                )}
                            </div>
                        </div>
                    )}
                </div>
            </div>

            {toast && <ToastEl msg={toast.msg} type={toast.type} onDone={() => setToast(null)} />}
        </div>
    );
}

// ─── SUB-COMPONENTS GIAO DIỆN (UI) TÁCH RỜI ────────────────────────────────
function RobotTable({ robots, onToggle }: { robots: RobotAsset[]; onToggle: (id: string) => void }) {
    return (
        <table className="w-full border-collapse" style={{ minWidth: 800 }}>
            <thead>
                <tr style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                    {["Robot ID", "Model", "MAC Address", "MQTT", "Maintenance"].map((h) => (
                        <th key={h} className="text-left px-4 py-3 font-mono text-[10px] font-semibold tracking-wider whitespace-nowrap" style={{ color: "var(--text-faint)" }}>{h}</th>
                    ))}
                </tr>
            </thead>
            <tbody>
                {robots.map((r) => (
                    <tr key={r.id} style={{ borderBottom: "1px solid var(--border-subtle)" }}>
                        <td className="px-4 py-3 font-mono text-[12px] font-bold" style={{ color: "#22d3ee" }}>{r.id}</td>
                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{r.model}</td>
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>{r.mac}</td>
                        <td className="px-4 py-3">
                            <span className="inline-flex items-center gap-1.5 font-mono text-[10px] font-bold px-2 py-1 rounded" style={{ background: r.mqtt === "ONLINE" ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)", color: r.mqtt === "ONLINE" ? "#22c55e" : "#ef4444", border: `1px solid ${r.mqtt === "ONLINE" ? "rgba(34,197,94,0.25)" : "rgba(239,68,68,0.25)"}` }}>
                                {r.mqtt}
                            </span>
                        </td>
                        <td className="px-4 py-3">
                            <button onClick={() => onToggle(r.id)} className="px-3 py-1 rounded-md text-[10px] font-bold" style={{ background: r.maintenance ? "#f59e0b" : "var(--surface-3)", color: r.maintenance ? "#fff" : "var(--text-muted)" }}>
                                {r.maintenance ? "BẢO TRÌ" : "SẴN SÀNG"}
                            </button>
                        </td>
                    </tr>
                ))}
            </tbody>
        </table>
    );
}

function ToastEl({ msg, type, onDone }: any) {
    useEffect(() => { const t = setTimeout(() => { onDone(); }, 3000); return () => clearTimeout(t); }, [onDone]);
    return (
        <div className="fixed bottom-6 right-6 z-[200] px-4 py-3 rounded-xl border border-gray-700 shadow-2xl" style={{ background: "var(--surface-2)", color: "#fff" }}>
            {msg}
        </div>
    );
}
