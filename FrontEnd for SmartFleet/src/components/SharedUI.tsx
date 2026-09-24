import { useState } from "react";
import { Icon, IC } from "./Icons";
import { Priority, TaskStatus } from "../data/mockData";

export function MetricCard({ label, value, sub, trend, trendUp, accentColor, glowColor, iconEl }: {
    label: string; value: string; sub: string; trend?: string; trendUp?: boolean;
    accentColor: string; glowColor: string; iconEl: React.ReactNode;
}) {
    return (
        <div
            className="flex-1 rounded-2xl p-4 relative overflow-hidden transition-colors"
            style={{
                background: "var(--surface-2)",
                border: "1px solid var(--border-subtle)",
            }}
        >
            {/* Glow overlay — reduced in light mode */}
            <div
                className="absolute top-0 right-0 w-32 h-32 rounded-full blur-3xl pointer-events-none"
                style={{
                    background: glowColor,
                    transform: "translate(30%,-30%)",
                    opacity: 0.08,
                }}
            />
            <div className="flex items-start justify-between mb-3">
                <div
                    className="w-10 h-10 rounded-xl flex items-center justify-center"
                    style={{
                        background: `${accentColor}18`,
                        border: `1px solid ${accentColor}30`,
                        color: accentColor,
                    }}
                >
                    {iconEl}
                </div>
                {trend && (
                    <span
                        className="text-[11px] font-mono font-semibold px-2 py-0.5 rounded-full"
                        style={{
                            background: trendUp ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)",
                            color: trendUp ? "#22c55e" : "#ef4444",
                            border: `1px solid ${trendUp ? "rgba(34,197,94,0.2)" : "rgba(239,68,68,0.2)"}`,
                        }}
                    >
                        {trendUp ? "↑" : "↓"} {trend}
                    </span>
                )}
            </div>
            <div className="font-mono text-[28px] font-bold leading-none mb-1" style={{ color: accentColor }}>
                {value}
            </div>
            <div className="text-[12px] font-semibold mb-0.5" style={{ color: "var(--text-primary)" }}>
                {label}
            </div>
            <div className="text-[11px]" style={{ color: "var(--text-faint)" }}>{sub}</div>
        </div>
    );
}

export function PriorityBadge({ priority }: { priority: Priority }) {
    const s: Record<Priority, [string, string]> = {
        HIGH: ["rgba(239,68,68,0.12)", "#f87171"],
        MEDIUM: ["rgba(245,158,11,0.12)", "#fbbf24"],
        LOW: ["rgba(100,116,139,0.12)", "#94a3b8"],
    };
    return (
        <span className="font-mono text-[10px] font-semibold px-2 py-0.5 rounded"
            style={{ background: s[priority][0], color: s[priority][1], border: `1px solid ${s[priority][1]}30` }}>
            {priority}
        </span>
    );
}

export function StatusBadge({ status }: { status: TaskStatus }) {
    const s: Record<TaskStatus, [string, string]> = {
        PENDING: ["rgba(100,116,139,0.12)", "#94a3b8"],
        QUEUED: ["rgba(59,130,246,0.12)", "#60a5fa"],
        ASSIGNED: ["rgba(168,85,247,0.12)", "#c084fc"],
        EXECUTING: ["rgba(34,211,238,0.12)", "#22d3ee"],
        COMPLETED: ["rgba(34,197,94,0.12)", "#4ade80"],
        CANCELLED: ["rgba(239,68,68,0.12)", "#ef4444"],
    };
    return (
        <span className="font-mono text-[10px] font-semibold px-2 py-0.5 rounded flex items-center gap-1.5 w-fit"
            style={{ background: s[status][0], color: s[status][1], border: `1px solid ${s[status][1]}30` }}>
            <span className="w-1.5 h-1.5 rounded-full" style={{ background: s[status][1], animation: status === "EXECUTING" ? "pulse 1.5s infinite" : "none" }} />
            {status}
        </span>
    );
}

export function PageHeader({ title, sub, children }: { title: string; sub: string; children?: React.ReactNode }) {
    return (
        <div
            className="shrink-0 flex items-center justify-between px-6 py-3 transition-colors"
            style={{ borderBottom: "1px solid var(--border-subtle)", background: "var(--surface-1)" }}
        >
            <div>
                <h1 className="text-[15px] font-bold tracking-tight" style={{ color: "var(--text-primary)" }}>{title}</h1>
                <p className="text-[11px] font-mono mt-0.5" style={{ color: "var(--text-faint)" }}>{sub}</p>
            </div>
            <div className="flex items-center gap-3">{children}</div>
        </div>
    );
}

export function PrimaryBtn({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
    const [hov, setHov] = useState(false);
    return (
        <button
            onClick={onClick}
            onMouseEnter={() => setHov(true)}
            onMouseLeave={() => setHov(false)}
            className="flex items-center gap-2 px-4 py-2 rounded-xl text-[13px] font-bold transition-colors"
            style={{
                background: hov ? "#38bdf8" : "#22d3ee",
                color: "#07091a",
                boxShadow: "0 4px 16px rgba(34,211,238,0.25)",
            }}
        >
            {children}
        </button>
    );
}