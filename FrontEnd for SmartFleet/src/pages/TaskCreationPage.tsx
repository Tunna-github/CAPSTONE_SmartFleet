import { useState } from "react";
import { Priority, DELIVERY_OPTIONS, PICKUP_OPTIONS } from "../data/mockData";
import { Icon, IC } from "../components/Icons";
import { useData } from "../context/DataContext";
import { useNavigate } from "react-router-dom";

export function TaskCreationPage() {
    const { addTask } = useData();
    const navigate = useNavigate();
    const [pickup, setPickup] = useState(PICKUP_OPTIONS[0]);
    const [delivery, setDelivery] = useState(DELIVERY_OPTIONS[0]);
    const [pkg, setPkg] = useState("");
    const [priority, setPriority] = useState<Priority>("MEDIUM");
    const [toast, setToast] = useState<string | null>(null);

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!pkg.trim()) { setToast("Package description is required."); return; }

        addTask(pickup, delivery, pkg, priority);
        setToast(`Task created: ${pickup} → ${delivery}`);
        setPkg("");

        setTimeout(() => {
            setToast(null);
            navigate("/dashboard"); // Go back to dashboard after creation
        }, 1500);
    };

    const priorityColors: Record<Priority, string> = { HIGH: "#f87171", MEDIUM: "#fbbf24", LOW: "#94a3b8" };

    return (
        <div className="flex flex-col h-full p-6 overflow-y-auto" style={{ background: "#070d1e" }}>
            <div className="max-w-2xl w-full mx-auto">
                <div className="mb-6">
                    <h1 className="text-[18px] font-bold text-white">Create Transport Task</h1>
                    <p className="text-[12px] font-mono mt-1" style={{ color: "#4a5a80" }}>Dispatch a new mission to the autonomous fleet.</p>
                </div>

                <form onSubmit={handleSubmit} className="rounded-2xl p-6 space-y-6" style={{ background: "#0c1128", border: "1px solid #151d35" }}>
                    <div>
                        <label className="block text-[11px] font-mono mb-2" style={{ color: "#4a5a80" }}>PICKUP LOCATION</label>
                        <select value={pickup} onChange={e => setPickup(e.target.value)} className="w-full px-3 py-2.5 rounded-lg text-[13px] text-white focus:outline-none appearance-none" style={{ background: "#07091a", border: "1px solid #151d35" }}>
                            {PICKUP_OPTIONS.map(o => <option key={o} value={o}>{o}</option>)}
                        </select>
                    </div>

                    <div>
                        <label className="block text-[11px] font-mono mb-2" style={{ color: "#4a5a80" }}>DELIVERY DESTINATION</label>
                        <select value={delivery} onChange={e => setDelivery(e.target.value)} className="w-full px-3 py-2.5 rounded-lg text-[13px] text-white focus:outline-none appearance-none" style={{ background: "#07091a", border: "1px solid #151d35" }}>
                            {DELIVERY_OPTIONS.map(o => <option key={o} value={o}>{o}</option>)}
                        </select>
                    </div>

                    <div>
                        <label className="block text-[11px] font-mono mb-2" style={{ color: "#4a5a80" }}>PACKAGE DESCRIPTION</label>
                        <input type="text" value={pkg} onChange={e => setPkg(e.target.value)} placeholder="e.g. Industrial Parts × 3" className="w-full px-3 py-2.5 rounded-lg text-[13px] text-white focus:outline-none" style={{ background: "#07091a", border: "1px solid #151d35" }} />
                    </div>

                    <div>
                        <label className="block text-[11px] font-mono mb-2" style={{ color: "#4a5a80" }}>PRIORITY LEVEL</label>
                        <div className="flex gap-3">
                            {(["HIGH", "MEDIUM", "LOW"] as Priority[]).map(p => {
                                const selected = priority === p;
                                const color = priorityColors[p];
                                return (
                                    <button key={p} type="button" onClick={() => setPriority(p)} className="flex-1 py-3 rounded-lg text-[12px] font-bold transition-all" style={{ background: selected ? `${color}20` : "#07091a", border: `2px solid ${selected ? color : "#151d35"}`, color: selected ? color : "#4a5a80" }}>
                                        {p}
                                    </button>
                                );
                            })}
                        </div>
                    </div>

                    <button type="submit" className="w-full py-3 rounded-xl text-[14px] font-bold transition-colors mt-2" style={{ background: "#22d3ee", color: "#07091a" }}>
                        + Dispatch Task
                    </button>
                </form>

                {toast && (
                    <div className="mt-4 p-3 rounded-lg text-[12px] font-mono flex items-center gap-2" style={{ background: "rgba(34,197,94,0.1)", border: "1px solid rgba(34,197,94,0.3)", color: "#4ade80" }}>
                        <Icon d={IC.check} size={14} /> {toast}
                    </div>
                )}
            </div>
        </div>
    );
}