import { RobotPos } from "../data/mockData";
import { useTheme } from "../context/ThemeContext";

const CELL = 40;
const GCOLS = 28;
const GROWS = 18;
const GW = CELL * GCOLS;
const GH = CELL * GROWS;

interface Cell { type: "floor" | "wall" | "shelf" | "charge" | "pickup" | "delivery" | "dock"; label?: string; color?: string; }

function buildGrid(): Cell[][] {
    const g: Cell[][] = Array.from({ length: GROWS }, () => Array.from({ length: GCOLS }, () => ({ type: "floor" })));
    for (let c = 0; c < GCOLS; c++) { g[0][c] = { type: "wall" }; g[GROWS - 1][c] = { type: "wall" }; }
    for (let r = 0; r < GROWS; r++) { g[r][0] = { type: "wall" }; g[r][GCOLS - 1] = { type: "wall" }; }

    const shelfZones = [
        { r1: 2, r2: 5, c1: 2, c2: 7, color: "#1e3a5f" }, { r1: 2, r2: 5, c1: 10, c2: 15, color: "#1f3a20" },
        { r1: 2, r2: 5, c1: 18, c2: 23, color: "#3a2060" }, { r1: 9, r2: 12, c1: 2, c2: 7, color: "#3a2020" },
        { r1: 9, r2: 12, c1: 10, c2: 15, color: "#2a3000" }, { r1: 9, r2: 12, c1: 18, c2: 23, color: "#002a3a" },
    ];
    shelfZones.forEach(({ r1, r2, c1, c2, color }) => {
        for (let r = r1; r <= r2; r++) for (let c = c1; c <= c2; c++) if ((r - r1) % 2 === 0) g[r][c] = { type: "shelf", color };
    });

    [{ r: 7, c: 4, label: "A-01" }, { r: 7, c: 12, label: "B-01" }, { r: 7, c: 20, label: "C-01" }, { r: 3, c: 25, label: "A-03" }].forEach(({ r, c, label }) => { g[r][c] = { type: "pickup", label }; });
    [{ r: 14, c: 4, label: "D-12" }, { r: 14, c: 12, label: "E-01" }, { r: 14, c: 20, label: "F-03" }, { r: 14, c: 25, label: "SHP" }].forEach(({ r, c, label }) => { g[r][c] = { type: "delivery", label }; });
    [{ r: 16, c: 13, label: "CHG1" }, { r: 16, c: 15, label: "CHG2" }].forEach(({ r, c, label }) => { g[r][c] = { type: "charge", label }; });
    g[9][26] = { type: "dock", label: "DOCK" };
    return g;
}

const GRID = buildGrid();
const ZONE_DEFS = [
    { label: "Zone A", r: 2, c: 2, color: "#0ea5e9" }, { label: "Zone B", r: 2, c: 10, color: "#22c55e" },
    { label: "Zone C", r: 2, c: 18, color: "#a855f7" }, { label: "Zone D", r: 9, c: 2, color: "#ef4444" },
    { label: "Zone E", r: 9, c: 10, color: "#eab308" }, { label: "Zone F", r: 9, c: 18, color: "#06b6d4" },
];

export function WarehouseGrid({ robots }: { robots: RobotPos[] }) {
    const { theme } = useTheme();
    const isLight = theme === "light";

    const cellTypeStyles: Record<string, { fill: string; stroke: string }> = isLight
        ? {
            floor: { fill: "#ffffff", stroke: "#e2e8f0" },
            wall: { fill: "#f1f5f9", stroke: "#cbd5e1" },
            shelf: { fill: "#e2e8f0", stroke: "#94a3b8" },
            pickup: { fill: "#dbeafe", stroke: "#3b82f6" },
            delivery: { fill: "#dcfce7", stroke: "#22c55e" },
            charge: { fill: "#fef3c7", stroke: "#f59e0b" },
            dock: { fill: "#ede9fe", stroke: "#8b5cf6" },
        }
        : {
            floor: { fill: "#060d1f", stroke: "#0d1634" },
            wall: { fill: "#0a1128", stroke: "#151d35" },
            shelf: { fill: "#0f1a30", stroke: "#1a2a4a" },
            pickup: { fill: "#1c3a6e", stroke: "#3b82f6" },
            delivery: { fill: "#1a4025", stroke: "#22c55e" },
            charge: { fill: "#3a2e00", stroke: "#f59e0b" },
            dock: { fill: "#2d1a5e", stroke: "#8b5cf6" },
        };

    // In light mode, override the shelf zone colors so they don't look like dark blobs
    const lightShelfOverrides: Record<string, string> = {
        "#1e3a5f": "#dbeafe",
        "#1f3a20": "#dcfce7",
        "#3a2060": "#ede9fe",
        "#3a2020": "#fee2e2",
        "#2a3000": "#fef3c7",
        "#002a3a": "#cffafe",
    };

    return (
        <svg
            width="100%"
            height="100%"
            viewBox={`0 0 ${GW} ${GH}`}
            style={{ background: isLight ? "#ffffff" : "#060d1f", display: "block", transition: "background 0.2s ease" }}
            preserveAspectRatio="xMidYMid meet"
        >
            <defs>
                <pattern id="dots" x="0" y="0" width={CELL} height={CELL} patternUnits="userSpaceOnUse">
                    <circle cx="0" cy="0" r="0.8" fill={isLight ? "#e2e8f0" : "#0d1634"} />
                </pattern>
                <filter id="glow-cyan" x="-50%" y="-50%" width="200%" height="200%">
                    <feGaussianBlur stdDeviation="3" result="blur" />
                    <feMerge><feMergeNode in="blur" /><feMergeNode in="SourceGraphic" /></feMerge>
                </filter>
                <filter id="glow-red" x="-50%" y="-50%" width="200%" height="200%">
                    <feGaussianBlur stdDeviation="2" result="blur" />
                    <feMerge><feMergeNode in="blur" /><feMergeNode in="SourceGraphic" /></feMerge>
                </filter>
                <filter id="glow-yellow" x="-50%" y="-50%" width="200%" height="200%">
                    <feGaussianBlur stdDeviation="2" result="blur" />
                    <feMerge><feMergeNode in="blur" /><feMergeNode in="SourceGraphic" /></feMerge>
                </filter>
                <marker id="arrow" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto">
                    <path d="M0,0 L4,2 L0,4 Z" fill="#22d3ee" opacity="0.5" />
                </marker>
            </defs>

            <rect width={GW} height={GH} fill="url(#dots)" />

            {GRID.map((row, ri) => row.map((cell, ci) => {
                const style = cellTypeStyles[cell.type] || cellTypeStyles.floor;
                const x = ci * CELL; const y = ri * CELL;
                // Convert the shelf color to a light equivalent if we're in light mode
                const shelfColor = cell.color
                    ? (isLight ? (lightShelfOverrides[cell.color] || style.fill) : cell.color)
                    : style.fill;
                return (
                    <g key={`${ri}-${ci}`}>
                        <rect x={x} y={y} width={CELL} height={CELL} fill={cell.type === "shelf" ? shelfColor : style.fill} stroke={style.stroke} strokeWidth="0.5" />
                        {cell.type === "shelf" && (
                            <>
                                <rect x={x + 3} y={y + 4} width={CELL - 6} height={6} rx="1" fill={style.stroke} opacity="0.5" />
                                <rect x={x + 3} y={y + 14} width={CELL - 6} height={6} rx="1" fill={style.stroke} opacity="0.5" />
                                <rect x={x + 3} y={y + 24} width={CELL - 6} height={6} rx="1" fill={style.stroke} opacity="0.5" />
                            </>
                        )}
                        {(cell.type === "pickup" || cell.type === "delivery" || cell.type === "charge" || cell.type === "dock") && (
                            <>
                                <rect x={x + 2} y={y + 2} width={CELL - 4} height={CELL - 4} rx="3" fill={style.fill} stroke={style.stroke} strokeWidth="1.5" />
                                <text x={x + CELL / 2} y={y + CELL / 2 + 4} textAnchor="middle" fontSize="8" fontFamily="monospace" fontWeight="bold" fill={style.stroke}>{cell.label}</text>
                            </>
                        )}
                    </g>
                );
            }))}

            {ZONE_DEFS.map(({ label, r, c, color }) => (
                <text key={label} x={c * CELL + 4} y={r * CELL + 12} fontSize="9" fontFamily="monospace" fontWeight="800" fill={color} opacity="0.7" letterSpacing="1">
                    {label.toUpperCase()}
                </text>
            ))}

            {robots.filter(rb => rb.missionId && rb.mqtt === "ONLINE").map(rb => (
                <line key={`path-${rb.id}`} x1={rb.x * CELL + CELL / 2} y1={rb.y * CELL + CELL / 2} x2={rb.tx * CELL + CELL / 2} y2={rb.ty * CELL + CELL / 2} stroke={rb.color} strokeWidth="1" strokeDasharray="4 3" opacity="0.25" markerEnd="url(#arrow)" />
            ))}

            {robots.map((rb) => {
                const cx = rb.x * CELL + CELL / 2; const cy = rb.y * CELL + CELL / 2;
                const filterId = rb.mqtt === "OFFLINE" ? "glow-red" : rb.battery <= 15 ? "glow-yellow" : "glow-cyan";
                const R = 11;
                return (
                    <g key={rb.id} filter={`url(#${filterId})`}>
                        {rb.mqtt === "ONLINE" && (
                            <circle cx={cx} cy={cy} r={R + 5} fill="none" stroke={rb.color} strokeWidth="0.8" opacity="0.3">
                                <animate attributeName="r" values={`${R + 3};${R + 10};${R + 3}`} dur="2s" repeatCount="indefinite" />
                                <animate attributeName="opacity" values="0.4;0;0.4" dur="2s" repeatCount="indefinite" />
                            </circle>
                        )}
                        <rect x={cx - R} y={cy - R} width={R * 2} height={R * 2} rx="4"
                            fill={rb.mqtt === "OFFLINE" ? "#1a0a0a" : (isLight ? "#ffffff" : "#0a1628")}
                            stroke={rb.color} strokeWidth="1.5" />
                        <text x={cx} y={cy + 4} textAnchor="middle" fontSize="12" fill={rb.color}>⬡</text>
                        <text x={cx} y={cy + R + 10} textAnchor="middle" fontSize="7" fontFamily="monospace" fontWeight="600" fill={rb.color} opacity="0.9">{rb.id.replace("AMR-", "")}</text>
                        <circle cx={cx + R - 3} cy={cy - R + 3} r="3" fill={rb.battery <= 15 ? "#ef4444" : rb.battery <= 35 ? "#f59e0b" : "#22c55e"} />
                    </g>
                );
            })}
        </svg>
    );
}