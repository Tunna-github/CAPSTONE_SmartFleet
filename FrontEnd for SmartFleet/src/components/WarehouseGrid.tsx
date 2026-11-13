import { useEffect, useMemo, useRef, useState } from "react";
import { RobotPos } from "../data/mockData";
import { useTheme } from "../context/ThemeContext";

const CELL = 40;
const GCOLS = 28;
const GROWS = 18;
const GW = CELL * GCOLS;
const GH = CELL * GROWS;

export type WarehouseCellType = "floor" | "wall" | "station";
export interface WarehouseCell { type: WarehouseCellType; label?: string; color?: string; }
export type WarehouseLayout = WarehouseCell[][];

export function createDefaultWarehouseLayout(): WarehouseLayout {
    const g: WarehouseLayout = Array.from({ length: GROWS }, () => Array.from({ length: GCOLS }, () => ({ type: "wall" })));
    for (let c = 0; c < GCOLS; c++) { g[0][c] = { type: "wall" }; g[GROWS - 1][c] = { type: "wall" }; }
    for (let r = 0; r < GROWS; r++) { g[r][0] = { type: "wall" }; g[r][GCOLS - 1] = { type: "wall" }; }

    [
        { r: 6, c: 9, label: "S1" }, { r: 6, c: 13, label: "S2" }, { r: 6, c: 17, label: "S3" },
        { r: 9, c: 9, label: "S4" }, { r: 9, c: 13, label: "S5" }, { r: 9, c: 17, label: "S6" },
        { r: 12, c: 9, label: "S7" }, { r: 12, c: 13, label: "S8" }, { r: 12, c: 17, label: "S9" },
    ].forEach(({ r, c, label }) => { g[r][c] = { type: "station", label }; });

    return g;
}

const DEFAULT_GRID = createDefaultWarehouseLayout();

function clampInt(n: unknown, min: number, max: number): number {
    const raw = typeof n === "number" ? n : Number.parseInt(String(n), 10);
    const v = Number.isFinite(raw) ? raw : min;
    return Math.max(min, Math.min(max, Math.round(v)));
}

function isWalkable(cell: WarehouseCell): boolean {
    return cell.type !== "wall";
}

function findPath(grid: WarehouseLayout, start: { r: number; c: number }, goal: { r: number; c: number }): Array<{ r: number; c: number }> | null {
    if (start.r === goal.r && start.c === goal.c) return [start];
    if (!grid[start.r]?.[start.c] || !grid[goal.r]?.[goal.c]) return null;
    if (!isWalkable(grid[start.r][start.c]) || !isWalkable(grid[goal.r][goal.c])) return null;

    const key = (r: number, c: number) => `${r},${c}`;
    const q: Array<{ r: number; c: number }> = [start];
    const visited = new Set<string>([key(start.r, start.c)]);
    const prev = new Map<string, string>();

    while (q.length) {
        const cur = q.shift()!;
        const neighbors = [
            { r: cur.r - 1, c: cur.c },
            { r: cur.r + 1, c: cur.c },
            { r: cur.r, c: cur.c - 1 },
            { r: cur.r, c: cur.c + 1 },
        ];
        for (const nb of neighbors) {
            if (nb.r < 0 || nb.r >= GROWS || nb.c < 0 || nb.c >= GCOLS) continue;
            if (!isWalkable(grid[nb.r][nb.c])) continue;
            const k = key(nb.r, nb.c);
            if (visited.has(k)) continue;
            visited.add(k);
            prev.set(k, key(cur.r, cur.c));
            if (nb.r === goal.r && nb.c === goal.c) {
                const out: Array<{ r: number; c: number }> = [{ r: goal.r, c: goal.c }];
                let at = k;
                while (prev.has(at)) {
                    const p = prev.get(at)!;
                    const [pr, pc] = p.split(",").map((x) => Number.parseInt(x, 10));
                    out.push({ r: pr, c: pc });
                    at = p;
                }
                out.reverse();
                return out;
            }
            q.push(nb);
        }
    }

    return null;
}

export function WarehouseGrid({
    robots,
    grid = DEFAULT_GRID,
    links,
    scale = 1,
    onCellClick,
    onStationClick,
    animateRobots = true,
}: {
    robots: RobotPos[];
    grid?: WarehouseLayout;
    links?: Array<[string, string]>;
    scale?: number;
    onCellClick?: (row: number, col: number) => void;
    onStationClick?: (label: string) => void;
    animateRobots?: boolean;
}) {
    const { theme } = useTheme();
    const isLight = theme === "light";

    const cellTypeStyles: Record<string, { fill: string; stroke: string }> = isLight
        ? {
            floor: { fill: "#ffffff", stroke: "#e2e8f0" },
            wall: { fill: "#f1f5f9", stroke: "#cbd5e1" },
            station: { fill: "#cffafe", stroke: "#06b6d4" },
        }
        : {
            floor: { fill: "#060d1f", stroke: "#0d1634" },
            wall: { fill: "#0a1128", stroke: "#151d35" },
            station: { fill: "#083344", stroke: "#06b6d4" },
        };

    const stations = useMemo(() => {
        const pts: Array<{ r: number; c: number; label: string }> = [];
        for (let r = 0; r < GROWS; r++) {
            for (let c = 0; c < GCOLS; c++) {
                const cell = grid[r]?.[c];
                if (cell?.type === "station" && cell.label) pts.push({ r, c, label: cell.label });
            }
        }
        pts.sort((a, b) => a.label.localeCompare(b.label, undefined, { numeric: true }));
        return pts;
    }, [grid]);

    const defaultLinks = useMemo(() => {
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
    }, [stations]);

    const linksToDraw = links ?? defaultLinks;

    const gridWithRoads = useMemo<WarehouseLayout>(() => {
        const next: WarehouseLayout = grid.map((row) => row.map((cell) => ({ ...cell })));
        const pos = new Map<string, { r: number; c: number }>();
        for (const s of stations) pos.set(s.label, { r: s.r, c: s.c });
        for (const [a, b] of linksToDraw) {
            const pa = pos.get(a);
            const pb = pos.get(b);
            if (!pa || !pb) continue;
            if (pa.r === pb.r) {
                const r = pa.r;
                const c1 = Math.min(pa.c, pb.c);
                const c2 = Math.max(pa.c, pb.c);
                for (let c = c1; c <= c2; c++) {
                    if (next[r]?.[c]?.type !== "station") next[r]![c] = { type: "floor" };
                }
            } else if (pa.c === pb.c) {
                const c = pa.c;
                const r1 = Math.min(pa.r, pb.r);
                const r2 = Math.max(pa.r, pb.r);
                for (let r = r1; r <= r2; r++) {
                    if (next[r]?.[c]?.type !== "station") next[r]![c] = { type: "floor" };
                }
            }
        }
        return next;
    }, [grid, linksToDraw, stations]);

    type AnimState = {
        key: string;
        points: Array<{ x: number; y: number }>;
        total: number;
        dist: number;
        dir: 1 | -1;
        last: number;
        x: number;
        y: number;
        color: string;
    };

    const anim = useRef(new Map<string, AnimState>());
    const raf = useRef<number | null>(null);
    const lastRender = useRef(0);
    const [, force] = useState(0);

    useEffect(() => {
        if (!animateRobots) return;
        const next = new Map<string, AnimState>();
        for (const rb of robots) {
            if (!rb.missionId || rb.mqtt !== "ONLINE") continue;
            const sr = clampInt(rb.y, 0, GROWS - 1);
            const sc = clampInt(rb.x, 0, GCOLS - 1);
            const gr = clampInt(rb.ty, 0, GROWS - 1);
            const gc = clampInt(rb.tx, 0, GCOLS - 1);
            const path = findPath(gridWithRoads, { r: sr, c: sc }, { r: gr, c: gc });
            if (!path || path.length < 2) continue;
            const pts = path.map((p) => ({ x: p.c * CELL + CELL / 2, y: p.r * CELL + CELL / 2 }));
            const total = (pts.length - 1) * CELL;
            const key = `${sr},${sc}->${gr},${gc}`;
            const prev = anim.current.get(rb.id);
            const now = performance.now();
            if (prev && prev.key === key) {
                next.set(rb.id, { ...prev, last: now, color: rb.color });
            } else {
                next.set(rb.id, { key, points: pts, total, dist: 0, dir: 1, last: now, x: pts[0].x, y: pts[0].y, color: rb.color });
            }
        }
        anim.current = next;
    }, [robots, grid, animateRobots]);

    useEffect(() => {
        if (!animateRobots) return;
        const speed = 120;
        const step = (now: number) => {
            const st = anim.current;
            for (const v of st.values()) {
                const dt = Math.max(0, (now - v.last) / 1000);
                v.last = now;
                v.dist += v.dir * speed * dt;
                if (v.dist >= v.total) {
                    v.dist = v.total;
                    v.dir = -1;
                } else if (v.dist <= 0) {
                    v.dist = 0;
                    v.dir = 1;
                }
                const seg = Math.min(v.points.length - 2, Math.max(0, Math.floor(v.dist / CELL)));
                const frac = (v.dist - seg * CELL) / CELL;
                const a = v.points[seg];
                const b = v.points[seg + 1];
                v.x = a.x + (b.x - a.x) * frac;
                v.y = a.y + (b.y - a.y) * frac;
            }
            if (now - lastRender.current >= 33) {
                lastRender.current = now;
                force((x) => (x + 1) % 1000000);
            }
            raf.current = requestAnimationFrame(step);
        };
        raf.current = requestAnimationFrame(step);
        return () => {
            if (raf.current != null) cancelAnimationFrame(raf.current);
            raf.current = null;
        };
    }, [animateRobots]);

    const centerX = GW / 2;
    const centerY = GH / 2;
    const transform = `translate(${centerX} ${centerY}) scale(${scale}) translate(${-centerX} ${-centerY})`;

    return (
        <svg width="100%" height="100%" viewBox={`0 0 ${GW} ${GH}`} style={{ background: isLight ? "#ffffff" : "#060d1f", display: "block", transition: "background 0.2s ease" }} preserveAspectRatio="xMidYMid meet">
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

            <g transform={transform}>
                <rect width={GW} height={GH} fill="url(#dots)" />

                {linksToDraw.map(([a, b]) => {
                    const pa = stations.find((s) => s.label === a);
                    const pb = stations.find((s) => s.label === b);
                    if (!pa || !pb) return null;
                    return (
                        <line
                            key={`${a}::${b}`}
                            x1={pa.c * CELL + CELL / 2}
                            y1={pa.r * CELL + CELL / 2}
                            x2={pb.c * CELL + CELL / 2}
                            y2={pb.r * CELL + CELL / 2}
                            stroke={isLight ? "rgba(6,182,212,0.35)" : "rgba(34,211,238,0.25)"}
                            strokeWidth="6"
                            strokeLinecap="round"
                            markerStart="url(#arrow)"
                            markerEnd="url(#arrow)"
                        />
                    );
                })}

                {gridWithRoads.map((row, ri) => row.map((cell, ci) => {
                    const style = cellTypeStyles[cell.type] || cellTypeStyles.floor;
                    const x = ci * CELL; const y = ri * CELL;
                    const isInteractive = !!onCellClick || !!onStationClick;
                    return (
                        <g key={`${ri}-${ci}`}>
                            <rect x={x} y={y} width={CELL} height={CELL} fill={style.fill} stroke={style.stroke} strokeWidth="0.5" />
                            {cell.type === "station" && (
                                <>
                                    <rect x={x + 2} y={y + 2} width={CELL - 4} height={CELL - 4} rx="3" fill={style.fill} stroke={style.stroke} strokeWidth="1.5" />
                                    <text x={x + CELL / 2} y={y + CELL / 2 + 4} textAnchor="middle" fontSize="8" fontFamily="monospace" fontWeight="bold" fill={style.stroke}>{cell.label}</text>
                                </>
                            )}
                            {isInteractive && (
                                <rect
                                    x={x}
                                    y={y}
                                    width={CELL}
                                    height={CELL}
                                    fill="transparent"
                                    onClick={() => {
                                        if (cell.type === "station" && cell.label && onStationClick) onStationClick(cell.label);
                                        if (onCellClick) onCellClick(ri, ci);
                                    }}
                                    style={{ cursor: "pointer" }}
                                />
                            )}
                        </g>
                    );
                }))}

            {robots
                .filter((rb) => rb.missionId && rb.mqtt === "ONLINE")
                .map((rb) => {
                    const sr = clampInt(rb.y, 0, GROWS - 1);
                    const sc = clampInt(rb.x, 0, GCOLS - 1);
                    const gr = clampInt(rb.ty, 0, GROWS - 1);
                    const gc = clampInt(rb.tx, 0, GCOLS - 1);
                    const path = findPath(gridWithRoads, { r: sr, c: sc }, { r: gr, c: gc });
                    if (!path || path.length < 2) {
                        return (
                            <line
                                key={`path-${rb.id}`}
                                x1={sc * CELL + CELL / 2}
                                y1={sr * CELL + CELL / 2}
                                x2={gc * CELL + CELL / 2}
                                y2={gr * CELL + CELL / 2}
                                stroke={rb.color}
                                strokeWidth="1"
                                strokeDasharray="4 3"
                                opacity="0.25"
                                markerEnd="url(#arrow)"
                            />
                        );
                    }
                    const points = path.map((p) => `${p.c * CELL + CELL / 2},${p.r * CELL + CELL / 2}`).join(" ");
                    return (
                        <polyline
                            key={`path-${rb.id}`}
                            points={points}
                            fill="none"
                            stroke={rb.color}
                            strokeWidth="1"
                            strokeDasharray="4 3"
                            opacity="0.25"
                            markerEnd="url(#arrow)"
                        />
                    );
                })}

            {robots.map((rb) => {
                const a = animateRobots ? anim.current.get(rb.id) : undefined;
                const cx = a ? a.x : rb.x * CELL + CELL / 2;
                const cy = a ? a.y : rb.y * CELL + CELL / 2;
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
            </g>
        </svg>
    );
}
