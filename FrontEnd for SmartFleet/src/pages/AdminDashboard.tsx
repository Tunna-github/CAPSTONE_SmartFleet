import { useState, useEffect } from "react";
import { useData } from "../context/DataContext";
import { Icon, IC } from "../components/Icons";
import { PageHeader, PrimaryBtn } from "../components/SharedUI";


type AdminTab = "robots" | "nodes";
type NodeType = "PICKUP" | "DELIVERY" | "CHARGING" | "DOCK" | "IDLE";

interface RobotAsset {
    id: string; model: string; mac: string; ip: string;
    dateAdded: string; firmware: string; maintenance: boolean; mqtt: "ONLINE" | "OFFLINE";
}
interface MapNode {
    id: string; label: string; type: NodeType;
    x: number; y: number; zone: string; active: boolean; dateAdded: string;
}

const SEED_ROBOT_ASSETS: RobotAsset[] = [
    { id: "AMR-001", model: "FleetBot X200", mac: "A4:C3:F0:85:AC:01", ip: "192.168.1.101", dateAdded: "2025-03-12", firmware: "v3.2.1", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-002", model: "FleetBot X200", mac: "A4:C3:F0:85:AC:02", ip: "192.168.1.102", dateAdded: "2025-03-12", firmware: "v3.2.1", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-003", model: "FleetBot X100", mac: "A4:C3:F0:85:AC:03", ip: "192.168.1.103", dateAdded: "2025-01-08", firmware: "v2.9.4", maintenance: true, mqtt: "OFFLINE" },
    { id: "AMR-004", model: "FleetBot X300", mac: "B2:1D:9E:4A:77:04", ip: "192.168.1.104", dateAdded: "2025-05-20", firmware: "v4.0.0", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-005", model: "FleetBot X300", mac: "B2:1D:9E:4A:77:05", ip: "192.168.1.105", dateAdded: "2025-05-20", firmware: "v4.0.0", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-006", model: "FleetBot X100", mac: "A4:C3:F0:85:AC:06", ip: "192.168.1.106", dateAdded: "2025-01-08", firmware: "v2.9.4", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-007", model: "FleetBot X200", mac: "A4:C3:F0:85:AC:07", ip: "192.168.1.107", dateAdded: "2025-03-12", firmware: "v3.2.1", maintenance: false, mqtt: "ONLINE" },
    { id: "AMR-008", model: "FleetBot X100", mac: "A4:C3:F0:85:AC:08", ip: "192.168.1.108", dateAdded: "2025-01-08", firmware: "v2.9.4", maintenance: true, mqtt: "OFFLINE" },
];

const SEED_NODES: MapNode[] = [
    { id: "NODE-A01", label: "Station A-01", type: "PICKUP", x: 2, y: 3, zone: "Zone A", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-A02", label: "Station A-02", type: "PICKUP", x: 2, y: 7, zone: "Zone A", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-A03", label: "Station A-03", type: "PICKUP", x: 5, y: 7, zone: "Zone A", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-B01", label: "Station B-01", type: "PICKUP", x: 10, y: 3, zone: "Zone B", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-B03", label: "Station B-03", type: "PICKUP", x: 10, y: 7, zone: "Zone B", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-D12", label: "Bay D-12", type: "DELIVERY", x: 4, y: 14, zone: "Zone D", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-E01", label: "Bay E-01", type: "DELIVERY", x: 12, y: 14, zone: "Zone E", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-F03", label: "Storage F-03", type: "DELIVERY", x: 20, y: 14, zone: "Zone F", active: false, dateAdded: "2025-02-14" },
    { id: "NODE-CHG1", label: "Charging 1", type: "CHARGING", x: 13, y: 16, zone: "Charge", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-CHG2", label: "Charging 2", type: "CHARGING", x: 15, y: 16, zone: "Charge", active: true, dateAdded: "2025-01-05" },
    { id: "NODE-DOCK", label: "Rcv. Dock", type: "DOCK", x: 26, y: 9, zone: "Dock", active: true, dateAdded: "2025-01-05" },
];

const MODEL_OPTIONS = ["FleetBot X100", "FleetBot X200", "FleetBot X300", "FleetBot X400 (Beta)"];
const NODE_TYPE_OPTIONS: NodeType[] = ["PICKUP", "DELIVERY", "CHARGING", "DOCK", "IDLE"];

export function AdminDashboard() {
    const { robots: ctxRobots, toggleMaintenance } = useData();
    const [tab, setTab] = useState<AdminTab>("robots");
    const [robots, setRobots] = useState<RobotAsset[]>(SEED_ROBOT_ASSETS);
    const [nodes, setNodes] = useState<MapNode[]>(SEED_NODES);
    const [search, setSearch] = useState("");
    const [addRobotOpen, setAddRobotOpen] = useState(false);
    const [addNodeOpen, setAddNodeOpen] = useState(false);
    const [editRobot, setEditRobot] = useState<RobotAsset | null>(null);
    const [editNode, setEditNode] = useState<MapNode | null>(null);
    const [deleteRobotTarget, setDeleteRobotTarget] = useState<RobotAsset | null>(null);
    const [deleteNodeTarget, setDeleteNodeTarget] = useState<MapNode | null>(null);
    const [toast, setToast] = useState<{ msg: string; type: "success" | "error" | "info" } | null>(null);

    // Add robot form
    const [newRId, setNewRId] = useState("");
    const [newRModel, setNewRModel] = useState(MODEL_OPTIONS[1]);
    const [newRMac, setNewRMac] = useState("");
    const [newRIp, setNewRIp] = useState("");

    // Edit robot form
    const [editRModel, setEditRModel] = useState("");
    const [editRMac, setEditRMac] = useState("");
    const [editRIp, setEditRIp] = useState("");

    // Add node form
    const [newNLabel, setNewNLabel] = useState("");
    const [newNType, setNewNType] = useState<NodeType>("PICKUP");
    const [newNZone, setNewNZone] = useState("Zone A");
    const [newNX, setNewNX] = useState("0");
    const [newNY, setNewNY] = useState("0");

    // Edit node form
    const [editNLabel, setEditNLabel] = useState("");
    const [editNType, setEditNType] = useState<NodeType>("PICKUP");
    const [editNZone, setEditNZone] = useState("");

    const openEditRobot = (r: RobotAsset) => {
        setEditRobot(r); setEditRModel(r.model); setEditRMac(r.mac); setEditRIp(r.ip);
    };
    const openEditNode = (n: MapNode) => {
        setEditNode(n); setEditNLabel(n.label); setEditNType(n.type); setEditNZone(n.zone);
    };

    const saveNewRobot = () => {
        if (!newRId.trim() || !newRMac.trim()) { setToast({ msg: "Robot ID and MAC are required.", type: "error" }); return; }
        setRobots((prev) => [...prev, {
            id: newRId.trim(), model: newRModel, mac: newRMac.trim(),
            ip: newRIp.trim() || "0.0.0.0",
            dateAdded: new Date().toISOString().slice(0, 10),
            firmware: "v4.0.0", maintenance: false, mqtt: "OFFLINE",
        }]);
        setAddRobotOpen(false); setNewRId(""); setNewRMac(""); setNewRIp("");
        setToast({ msg: `${newRId.trim()} registered successfully.`, type: "success" });
    };

    const saveEditRobot = () => {
        if (!editRobot) return;
        setRobots((prev) => prev.map((r) =>
            r.id === editRobot.id ? { ...r, model: editRModel, mac: editRMac, ip: editRIp } : r
        ));
        setEditRobot(null);
        setToast({ msg: `${editRobot.id} updated.`, type: "success" });
    };

    const handleToggleMaintenance = (id: string) => {
        setRobots((prev) => prev.map((r) => r.id === id ? { ...r, maintenance: !r.maintenance, mqtt: !r.maintenance ? "OFFLINE" : "ONLINE" } : r));
        toggleMaintenance(id);
        setToast({ msg: `Maintenance ${robots.find((r) => r.id === id)?.maintenance ? "disabled" : "enabled"} for ${id}.`, type: "info" });
    };

    const handleDeleteRobot = () => {
        if (!deleteRobotTarget) return;
        setRobots((prev) => prev.filter((r) => r.id !== deleteRobotTarget.id));
        setToast({ msg: `${deleteRobotTarget.id} removed.`, type: "error" });
        setDeleteRobotTarget(null);
    };

    const saveNewNode = () => {
        if (!newNLabel.trim()) { setToast({ msg: "Node label is required.", type: "error" }); return; }
        const id = `NODE-${Date.now().toString(36).toUpperCase()}`;
        setNodes((prev) => [...prev, {
            id, label: newNLabel.trim(), type: newNType,
            x: parseInt(newNX) || 0, y: parseInt(newNY) || 0,
            zone: newNZone, active: true,
            dateAdded: new Date().toISOString().slice(0, 10),
        }]);
        setAddNodeOpen(false); setNewNLabel("");
        setToast({ msg: `Node "${newNLabel.trim()}" added.`, type: "success" });
    };

    const saveEditNode = () => {
        if (!editNode) return;
        setNodes((prev) => prev.map((n) =>
            n.id === editNode.id ? { ...n, label: editNLabel, type: editNType, zone: editNZone } : n
        ));
        setEditNode(null);
        setToast({ msg: `Node "${editNLabel}" updated.`, type: "success" });
    };

    const toggleNode = (id: string) => {
        setNodes((prev) => prev.map((n) => n.id === id ? { ...n, active: !n.active } : n));
    };

    const handleDeleteNode = () => {
        if (!deleteNodeTarget) return;
        setNodes((prev) => prev.filter((n) => n.id !== deleteNodeTarget.id));
        setToast({ msg: `Node removed.`, type: "error" });
        setDeleteNodeTarget(null);
    };

    const filteredRobots = robots.filter((r) =>
        !search || r.id.toLowerCase().includes(search.toLowerCase())
        || r.model.toLowerCase().includes(search.toLowerCase())
        || r.mac.toLowerCase().includes(search.toLowerCase())
    );
    const filteredNodes = nodes.filter((n) =>
        !search || n.id.toLowerCase().includes(search.toLowerCase())
        || n.label.toLowerCase().includes(search.toLowerCase())
        || n.zone.toLowerCase().includes(search.toLowerCase())
    );

    return (
        <div
            className="flex flex-col h-full overflow-hidden transition-colors"
            style={{ background: "var(--background)" }}
        >
            <PageHeader title="Admin CRUD Management" sub="Robot asset registry · Warehouse map node configuration">
                {tab === "robots"
                    ? <PrimaryBtn onClick={() => setAddRobotOpen(true)}><Icon d={IC.plus} size={15} /> Add New Robot</PrimaryBtn>
                    : <PrimaryBtn onClick={() => setAddNodeOpen(true)}><Icon d={IC.plus} size={15} /> Add Map Node</PrimaryBtn>
                }
            </PageHeader>

            {/* Tabs */}
            <div
                className="shrink-0 flex px-6"
                style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
            >
                {([["robots", "Robot Assets", IC.robots], ["nodes", "Map Nodes", IC.map]] as const).map(([id, label, icon]) => {
                    const act = tab === id;
                    return (
                        <button
                            key={id}
                            onClick={() => { setTab(id); setSearch(""); }}
                            className="flex items-center gap-2 px-5 py-3.5 text-[13px] font-semibold transition-all"
                            style={{
                                color: act ? "#22d3ee" : "var(--text-faint)",
                                background: "transparent",
                                border: "none",
                                borderBottom: act ? "2px solid #22d3ee" : "2px solid transparent",
                                marginBottom: -1,
                            }}
                        >
                            <Icon d={icon} size={14} />
                            {label}
                            <span
                                className="font-mono text-[10px] px-1.5 py-0.5 rounded"
                                style={{
                                    background: act ? "rgba(34,211,238,0.15)" : "var(--surface-3)",
                                    color: act ? "#22d3ee" : "var(--text-faint)",
                                    border: `1px solid ${act ? "rgba(34,211,238,0.25)" : "var(--border-medium)"}`,
                                }}
                            >
                                {id === "robots" ? robots.length : nodes.length}
                            </span>
                        </button>
                    );
                })}
            </div>

            {/* Search */}
            <div
                className="shrink-0 flex items-center gap-3 px-6 py-3"
                style={{ borderBottom: "1px solid var(--border-subtle)" }}
            >
                <div className="relative" style={{ width: 320 }}>
                    <span
                        className="absolute left-3 top-1/2 -translate-y-1/2"
                        style={{ color: "var(--text-faint)" }}
                    >
                        <Icon d={IC.search} size={14} />
                    </span>
                    <input
                        value={search}
                        onChange={(e) => setSearch(e.target.value)}
                        placeholder={tab === "robots" ? "Search robots, models, MAC…" : "Search nodes, zones…"}
                        className="w-full pl-9 pr-3 py-2 rounded-lg text-[13px] outline-none transition-colors"
                        style={{
                            background: "var(--surface-3)",
                            border: "1px solid var(--border-medium)",
                            color: "var(--text-primary)",
                        }}
                    />
                </div>
                <span className="font-mono text-[11px]" style={{ color: "var(--text-faint)" }}>
                    {tab === "robots" ? `${filteredRobots.length} robots` : `${filteredNodes.length} nodes`}
                </span>
            </div>

            {/* Table */}
            <div className="flex-1 overflow-hidden px-6 pb-6 pt-4">
                <div
                    className="h-full rounded-2xl overflow-hidden flex flex-col transition-colors"
                    style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                >
                    <div className="overflow-auto flex-1">
                        {tab === "robots" ? (
                            <RobotTable
                                robots={filteredRobots}
                                onEdit={openEditRobot}
                                onDelete={setDeleteRobotTarget}
                                onToggle={handleToggleMaintenance}
                            />
                        ) : (
                            <NodeTable
                                nodes={filteredNodes}
                                onEdit={openEditNode}
                                onDelete={setDeleteNodeTarget}
                                onToggle={toggleNode}
                            />
                        )}
                    </div>
                    <div
                        className="flex justify-between px-4 py-2.5 shrink-0"
                        style={{ borderTop: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
                    >
                        <span className="font-mono text-[11px]" style={{ color: "var(--text-faint)" }}>
                            {tab === "robots" ? `${filteredRobots.length} of ${robots.length} robots` : `${filteredNodes.length} of ${nodes.length} nodes`}
                        </span>
                        <span className="font-mono text-[11px]" style={{ color: "var(--text-faint)" }}>
                            {tab === "robots"
                                ? `${robots.filter((r) => r.mqtt === "ONLINE").length} online · ${robots.filter((r) => r.maintenance).length} in maintenance`
                                : `${nodes.filter((n) => n.active).length} active · ${nodes.filter((n) => !n.active).length} disabled`}
                        </span>
                    </div>
                </div>
            </div>

            {/* ══ Modals ══ */}
            {addRobotOpen && (
                <ModalShell title="Register New Robot" sub="Robot will be OFFLINE until first MQTT handshake." onClose={() => setAddRobotOpen(false)}>
                    <div className="p-6 space-y-4">
                        <div className="grid grid-cols-2 gap-4">
                            <InputField label="Robot ID" value={newRId} onChange={setNewRId} placeholder="AMR-009" />
                            <SelectField label="Model Type" value={newRModel} onChange={setNewRModel} options={MODEL_OPTIONS} />
                        </div>
                        <InputField label="MAC Address" value={newRMac} onChange={setNewRMac} placeholder="AA:BB:CC:DD:EE:FF" />
                        <InputField label="IP Address (optional)" value={newRIp} onChange={setNewRIp} placeholder="192.168.1.x" />
                    </div>
                    <ModalFooter
                        onCancel={() => setAddRobotOpen(false)}
                        onConfirm={saveNewRobot}
                        confirmLabel="Save & Register"
                    />
                </ModalShell>
            )}

            {editRobot && (
                <ModalShell title={`Edit Robot — ${editRobot.id}`} sub="Update model, MAC, or IP." onClose={() => setEditRobot(null)}>
                    <div className="p-6 space-y-4">
                        <SelectField label="Model Type" value={editRModel} onChange={setEditRModel} options={MODEL_OPTIONS} />
                        <InputField label="MAC Address" value={editRMac} onChange={setEditRMac} />
                        <InputField label="IP Address" value={editRIp} onChange={setEditRIp} />
                    </div>
                    <ModalFooter
                        onCancel={() => setEditRobot(null)}
                        onConfirm={saveEditRobot}
                        confirmLabel="✓ Save Changes"
                    />
                </ModalShell>
            )}

            {addNodeOpen && (
                <ModalShell title="Add Warehouse Map Node" sub="Immediately available for task routing." onClose={() => setAddNodeOpen(false)}>
                    <div className="p-6 space-y-4">
                        <InputField label="Node Label" value={newNLabel} onChange={setNewNLabel} placeholder="e.g. Station G-01" />
                        <div className="grid grid-cols-2 gap-4">
                            <SelectField label="Node Type" value={newNType} onChange={(v) => setNewNType(v as NodeType)} options={NODE_TYPE_OPTIONS} />
                            <InputField label="Zone" value={newNZone} onChange={setNewNZone} placeholder="Zone G" />
                        </div>
                        <div className="grid grid-cols-2 gap-4">
                            <InputField label="Grid X" value={newNX} onChange={setNewNX} placeholder="0" />
                            <InputField label="Grid Y" value={newNY} onChange={setNewNY} placeholder="0" />
                        </div>
                    </div>
                    <ModalFooter
                        onCancel={() => setAddNodeOpen(false)}
                        onConfirm={saveNewNode}
                        confirmLabel="Save Map Node"
                    />
                </ModalShell>
            )}

            {editNode && (
                <ModalShell title={`Edit Node — ${editNode.id}`} sub="Update node metadata." onClose={() => setEditNode(null)}>
                    <div className="p-6 space-y-4">
                        <InputField label="Node Label" value={editNLabel} onChange={setEditNLabel} />
                        <div className="grid grid-cols-2 gap-4">
                            <SelectField label="Node Type" value={editNType} onChange={(v) => setEditNType(v as NodeType)} options={NODE_TYPE_OPTIONS} />
                            <InputField label="Zone" value={editNZone} onChange={setEditNZone} />
                        </div>
                    </div>
                    <ModalFooter
                        onCancel={() => setEditNode(null)}
                        onConfirm={saveEditNode}
                        confirmLabel="✓ Save Changes"
                    />
                </ModalShell>
            )}

            {deleteRobotTarget && (
                <DeleteConfirm
                    label={deleteRobotTarget.id}
                    onCancel={() => setDeleteRobotTarget(null)}
                    onConfirm={handleDeleteRobot}
                />
            )}
            {deleteNodeTarget && (
                <DeleteConfirm
                    label={deleteNodeTarget.label}
                    onCancel={() => setDeleteNodeTarget(null)}
                    onConfirm={handleDeleteNode}
                />
            )}

            {toast && <ToastEl msg={toast.msg} type={toast.type} onDone={() => setToast(null)} />}
        </div>
    );
}

// ─── Sub-components ──────────────────────────────────────────────────────────

function RobotTable({
    robots, onEdit, onDelete, onToggle,
}: { robots: RobotAsset[]; onEdit: (r: RobotAsset) => void; onDelete: (r: RobotAsset) => void; onToggle: (id: string) => void }) {
    return (
        <table className="w-full border-collapse" style={{ minWidth: 1000 }}>
            <thead>
                <tr style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                    {["Robot ID", "Model", "MAC Address", "IP Address", "Firmware", "Date Added", "MQTT", "Maintenance", "Actions"].map((h) => (
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
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>{r.ip}</td>
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>{r.firmware}</td>
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>{r.dateAdded}</td>
                        <td className="px-4 py-3">
                            <span
                                className="inline-flex items-center gap-1.5 font-mono text-[10px] font-bold px-2 py-1 rounded"
                                style={{
                                    background: r.mqtt === "ONLINE" ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)",
                                    color: r.mqtt === "ONLINE" ? "#22c55e" : "#ef4444",
                                    border: `1px solid ${r.mqtt === "ONLINE" ? "rgba(34,197,94,0.25)" : "rgba(239,68,68,0.25)"}`,
                                }}
                            >
                                <span className="w-1.5 h-1.5 rounded-full" style={{ background: r.mqtt === "ONLINE" ? "#22c55e" : "#ef4444" }} />
                                {r.mqtt}
                            </span>
                        </td>
                        <td className="px-4 py-3">
                            <button
                                onClick={() => onToggle(r.id)}
                                className="flex items-center gap-2 px-2.5 py-1 rounded-md transition-colors"
                                style={{
                                    background: r.maintenance ? "rgba(245,158,11,0.12)" : "rgba(34,197,94,0.08)",
                                    border: `1px solid ${r.maintenance ? "rgba(245,158,11,0.3)" : "rgba(34,197,94,0.2)"}`,
                                }}
                            >
                                <div className="w-7 h-4 rounded-full relative transition-colors" style={{ background: r.maintenance ? "#f59e0b" : "var(--border-strong)" }}>
                                    <span
                                        className="absolute top-0.5 w-3 h-3 rounded-full bg-white transition-transform"
                                        style={{ left: 2, transform: r.maintenance ? "translateX(12px)" : "translateX(0)" }}
                                    />
                                </div>
                                <span className="font-mono text-[10px] font-bold" style={{ color: r.maintenance ? "#fbbf24" : "#22c55e" }}>
                                    {r.maintenance ? "ON" : "OFF"}
                                </span>
                            </button>
                        </td>
                        <td className="px-4 py-3">
                            <div className="flex gap-1.5">
                                <button
                                    onClick={() => onEdit(r)}
                                    className="w-7 h-7 rounded-md flex items-center justify-center transition-colors"
                                    style={{ background: "rgba(99,102,241,0.1)", color: "#818cf8", border: "1px solid rgba(99,102,241,0.3)" }}
                                >
                                    <Icon d={IC.pencil} size={12} />
                                </button>
                                <button
                                    onClick={() => onDelete(r)}
                                    className="w-7 h-7 rounded-md flex items-center justify-center transition-colors"
                                    style={{ background: "rgba(239,68,68,0.1)", color: "#ef4444", border: "1px solid rgba(239,68,68,0.3)" }}
                                >
                                    <Icon d={IC.trash} size={12} />
                                </button>
                            </div>
                        </td>
                    </tr>
                ))}
            </tbody>
        </table>
    );
}

function NodeTable({
    nodes, onEdit, onDelete, onToggle,
}: { nodes: MapNode[]; onEdit: (n: MapNode) => void; onDelete: (n: MapNode) => void; onToggle: (id: string) => void }) {
    const typeColors: Record<NodeType, string> = {
        PICKUP: "#60a5fa", DELIVERY: "#22c55e", CHARGING: "#fbbf24", DOCK: "#c084fc", IDLE: "#94a3b8",
    };
    return (
        <table className="w-full border-collapse" style={{ minWidth: 900 }}>
            <thead>
                <tr style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}>
                    {["Node ID", "Label", "Type", "Zone", "Position", "Date Added", "Active", "Actions"].map((h) => (
                        <th key={h} className="text-left px-4 py-3 font-mono text-[10px] font-semibold tracking-wider whitespace-nowrap" style={{ color: "var(--text-faint)" }}>{h}</th>
                    ))}
                </tr>
            </thead>
            <tbody>
                {nodes.map((n) => (
                    <tr key={n.id} style={{ borderBottom: "1px solid var(--border-subtle)", opacity: n.active ? 1 : 0.55 }}>
                        <td className="px-4 py-3 font-mono text-[12px] font-bold" style={{ color: "#22d3ee" }}>{n.id}</td>
                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{n.label}</td>
                        <td className="px-4 py-3">
                            <span
                                className="font-mono text-[10px] font-bold px-2 py-0.5 rounded"
                                style={{
                                    color: typeColors[n.type],
                                    background: `${typeColors[n.type]}14`,
                                    border: `1px solid ${typeColors[n.type]}28`,
                                }}
                            >
                                {n.type}
                            </span>
                        </td>
                        <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-muted)" }}>{n.zone}</td>
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>({n.x}, {n.y})</td>
                        <td className="px-4 py-3 font-mono text-[11px]" style={{ color: "var(--text-muted)" }}>{n.dateAdded}</td>
                        <td className="px-4 py-3">
                            <button
                                onClick={() => onToggle(n.id)}
                                className="w-9 h-5 rounded-full relative transition-colors"
                                style={{ background: n.active ? "#22c55e" : "var(--border-strong)", border: "none" }}
                            >
                                <span
                                    className="absolute top-0.5 w-4 h-4 rounded-full bg-white transition-transform"
                                    style={{ left: 2, transform: n.active ? "translateX(16px)" : "translateX(0)" }}
                                />
                            </button>
                        </td>
                        <td className="px-4 py-3">
                            <div className="flex gap-1.5">
                                <button
                                    onClick={() => onEdit(n)}
                                    className="w-7 h-7 rounded-md flex items-center justify-center transition-colors"
                                    style={{ background: "rgba(99,102,241,0.1)", color: "#818cf8", border: "1px solid rgba(99,102,241,0.3)" }}
                                >
                                    <Icon d={IC.pencil} size={12} />
                                </button>
                                <button
                                    onClick={() => onDelete(n)}
                                    className="w-7 h-7 rounded-md flex items-center justify-center transition-colors"
                                    style={{ background: "rgba(239,68,68,0.1)", color: "#ef4444", border: "1px solid rgba(239,68,68,0.3)" }}
                                >
                                    <Icon d={IC.trash} size={12} />
                                </button>
                            </div>
                        </td>
                    </tr>
                ))}
            </tbody>
        </table>
    );
}

// ─── Shared modal bits ───────────────────────────────────────────────────────

function ModalShell({
    title, sub, onClose, children, width = 500,
}: { title: string; sub?: string; onClose: () => void; children: React.ReactNode; width?: number }) {
    return (
        <div
            className="fixed inset-0 flex items-center justify-center z-[100] p-6"
            style={{ background: "rgba(0,0,0,0.75)", backdropFilter: "blur(6px)" }}
            onClick={onClose}
        >
            <div
                className="w-full rounded-2xl overflow-hidden"
                style={{
                    maxWidth: width,
                    background: "var(--surface-2)",
                    border: "1px solid var(--border-medium)",
                    boxShadow: "0 32px 80px rgba(0,0,0,0.6)",
                }}
                onClick={(e) => e.stopPropagation()}
            >
                <div
                    className="flex items-start justify-between px-6 py-5"
                    style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
                >
                    <div>
                        <h2 className="text-[15px] font-bold" style={{ color: "var(--text-primary)" }}>{title}</h2>
                        {sub && <p className="font-mono text-[11px] mt-0.5" style={{ color: "var(--text-faint)" }}>{sub}</p>}
                    </div>
                    <button
                        onClick={onClose}
                        className="w-7 h-7 rounded-lg flex items-center justify-center transition-colors"
                        style={{ background: "var(--surface-3)", color: "var(--text-muted)", border: "1px solid var(--border-medium)" }}
                    >
                        <Icon d={IC.x} size={14} />
                    </button>
                </div>
                {children}
            </div>
        </div>
    );
}

function ModalFooter({
    onCancel, onConfirm, confirmLabel,
}: { onCancel: () => void; onConfirm: () => void; confirmLabel: string }) {
    return (
        <div className="flex gap-3 px-6 py-4" style={{ borderTop: "1px solid var(--border-subtle)" }}>
            <button
                onClick={onCancel}
                className="flex-1 py-2.5 rounded-lg text-[13px] font-semibold"
                style={{ background: "var(--surface-3)", color: "var(--text-muted)", border: "1px solid var(--border-medium)" }}
            >
                Cancel
            </button>
            <button
                onClick={onConfirm}
                className="flex-[2] py-2.5 rounded-lg text-[13px] font-bold"
                style={{ background: "linear-gradient(135deg,#22d3ee,#3b82f6)", color: "#ffffff", boxShadow: "0 4px 16px rgba(34,211,238,0.3)" }}
            >
                {confirmLabel}
            </button>
        </div>
    );
}

function DeleteConfirm({
    label, onCancel, onConfirm,
}: { label: string; onCancel: () => void; onConfirm: () => void }) {
    return (
        <ModalShell title="Confirm Deletion" sub="This action cannot be undone." onClose={onCancel} width={400}>
            <div className="p-6 text-center">
                <div
                    className="w-14 h-14 rounded-full flex items-center justify-center mx-auto mb-4"
                    style={{ background: "rgba(239,68,68,0.12)", border: "1px solid rgba(239,68,68,0.3)", color: "#f87171" }}
                >
                    <Icon d={IC.trash} size={24} />
                </div>
                <p className="text-[13px] leading-relaxed mb-6" style={{ color: "var(--text-muted)" }}>
                    You are about to permanently delete <br />
                    <strong style={{ color: "var(--text-primary)" }}>{label}</strong>.
                </p>
                <div className="flex gap-3">
                    <button
                        onClick={onCancel}
                        className="flex-1 py-2.5 rounded-lg text-[13px] font-semibold"
                        style={{ background: "var(--surface-3)", color: "var(--text-muted)", border: "1px solid var(--border-medium)" }}
                    >
                        Cancel
                    </button>
                    <button
                        onClick={onConfirm}
                        className="flex-1 py-2.5 rounded-lg text-[13px] font-bold"
                        style={{ background: "rgba(239,68,68,0.15)", color: "#f87171", border: "1px solid rgba(239,68,68,0.4)" }}
                    >
                        Delete
                    </button>
                </div>
            </div>
        </ModalShell>
    );
}

function SelectField({
    label, value, onChange, options,
}: { label: string; value: string; onChange: (v: string) => void; options: string[] }) {
    return (
        <div>
            <label className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5" style={{ color: "var(--text-faint)" }}>
                {label}
            </label>
            <select
                value={value}
                onChange={(e) => onChange(e.target.value)}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] outline-none appearance-none cursor-pointer transition-colors"
                style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
            >
                {options.map((o) => <option key={o} value={o} style={{ background: "var(--surface-2)" }}>{o}</option>)}
            </select>
        </div>
    );
}

function InputField({
    label, value, onChange, placeholder,
}: { label: string; value: string; onChange: (v: string) => void; placeholder?: string }) {
    return (
        <div>
            <label className="block font-mono text-[10px] font-bold uppercase tracking-wider mb-1.5" style={{ color: "var(--text-faint)" }}>
                {label}
            </label>
            <input
                type="text"
                value={value}
                onChange={(e) => onChange(e.target.value)}
                placeholder={placeholder}
                className="w-full px-3 py-2.5 rounded-lg text-[13px] outline-none transition-colors"
                style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
            />
        </div>
    );
}

function ToastEl({ msg, type, onDone }: { msg: string; type: "success" | "error" | "info"; onDone: () => void }) {
    const [visible, setVisible] = useState(true);
    useEffect(() => {
        const t = setTimeout(() => { setVisible(false); setTimeout(onDone, 200); }, 3000);
        return () => clearTimeout(t);
    }, [onDone]);

    const colors = {
        success: { bg: "rgba(34,197,94,0.12)", border: "rgba(34,197,94,0.4)", color: "#22c55e", icon: IC.check },
        error: { bg: "rgba(239,68,68,0.12)", border: "rgba(239,68,68,0.4)", color: "#ef4444", icon: IC.x },
        info: { bg: "rgba(99,102,241,0.12)", border: "rgba(99,102,241,0.4)", color: "#818cf8", icon: IC.info },
    };
    const c = colors[type];

    return (
        <div
            className="fixed bottom-6 right-6 z-[200] flex items-center gap-2.5 px-4 py-3 rounded-xl transition-all"
            style={{
                background: "var(--surface-2)",
                border: `1px solid ${c.border}`,
                boxShadow: "0 8px 32px rgba(0,0,0,0.4)",
                opacity: visible ? 1 : 0,
                transform: visible ? "translateY(0)" : "translateY(20px)",
                minWidth: 280,
            }}
        >
            <span className="w-7 h-7 rounded-lg flex items-center justify-center shrink-0" style={{ background: c.bg, color: c.color }}>
                <Icon d={c.icon} size={14} />
            </span>
            <span className="text-[13px]" style={{ color: "var(--text-primary)" }}>{msg}</span>
        </div>
    );
}