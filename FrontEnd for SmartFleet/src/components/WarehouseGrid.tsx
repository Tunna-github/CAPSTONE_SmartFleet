import { useEffect, useMemo, useRef, useState } from "react";
import { RobotPos } from "../data/mockData";
import { stationRoles, useCatalog, type Catalog } from "../api/modules/catalog";
import type { CheckpointRole } from "../api/modules/navigation";
import { useTheme } from "../context/ThemeContext";

const CELL = 40;
const GCOLS = 28;
const GROWS = 18;
const GW = CELL * GCOLS;
const GH = CELL * GROWS;

export type WarehouseCellType = "floor" | "wall" | "station" | "qr" | "charge";
export interface WarehouseCell { type: WarehouseCellType; label?: string; displayName?: string; operationalRole?: CheckpointRole; color?: string; arucoId?: number; isIntersection?: boolean; markerYawDeg?: number; markerSizeM?: number; }
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
    g[15][13] = { type: "charge", label: "CHG1" };

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
    directedLinks = false,
    scale = 1,
    panOffset = { x: 0, y: 0 },
    showDirections = true,
    onCellClick,
    onStationClick,
    onPointClick,
    selectedCell,
    selectedPoint,
    animateRobots = true,
    missionRoutes = [],
    onRobotClick,
    focusQrNetwork = false,
    qrOnly = focusQrNetwork,
    catalog: suppliedCatalog,
}: {
    robots: RobotPos[];
    missionRoutes?: { robotId: string; color: string; points: { x: number; y: number }[] }[];
    onRobotClick?: (robotCode: string) => void;
    focusQrNetwork?: boolean;
    qrOnly?: boolean;
    catalog?: Catalog | null;
    grid?: WarehouseLayout;
    links?: Array<[string, string]>;
    directedLinks?: boolean;
    scale?: number;
    panOffset?: { x: number; y: number };
    showDirections?: boolean;
    onCellClick?: (row: number, col: number) => void;
    onStationClick?: (label: string) => void;
    onPointClick?: (label: string) => void;
    selectedCell?: { row: number; col: number } | null;
    selectedPoint?: string | null;
    animateRobots?: boolean;
}) {
    const { theme } = useTheme();
    const { catalog: loadedCatalog } = useCatalog(suppliedCatalog === undefined);
    const catalog = suppliedCatalog === undefined ? loadedCatalog : suppliedCatalog;
    const stationByMarker = useMemo(() => new Map(catalog?.stations
        .filter(station => station.warehouseId === catalog.navigationWarehouseId && station.arucoId !== null)
        .map(station => [station.arucoId, station])), [catalog]);
    const isLight = theme === "light";
    const handlePointClick = onPointClick ?? onStationClick;
    const compactLabels = directedLinks && focusQrNetwork && !onCellClick && !handlePointClick;

    const cellTypeStyles = useMemo<Record<string, { fill: string; stroke: string }>>(() => isLight
        ? {
            floor: { fill: "#ffffff", stroke: "#e2e8f0" },
            wall: { fill: "#f1f5f9", stroke: "#cbd5e1" },
            station: { fill: "#cffafe", stroke: "#06b6d4" },
            qr: { fill: "#ede9fe", stroke: "#8b5cf6" },
            charge: { fill: "#fef3c7", stroke: "#f59e0b" },
        }
        : {
            floor: { fill: "#060d1f", stroke: "#0d1634" },
            wall: { fill: "#0a1128", stroke: "#151d35" },
            station: { fill: "#083344", stroke: "#06b6d4" },
            qr: { fill: "#2d1a5e", stroke: "#c084fc" },
            charge: { fill: "#3a2e00", stroke: "#f59e0b" },
        }, [isLight]);

    const stations = useMemo(() => {
        const pts: Array<{ r: number; c: number; label: string }> = [];
        for (let r = 0; r < GROWS; r++) {
            for (let c = 0; c < GCOLS; c++) {
                const cell = grid[r]?.[c];
                if ((qrOnly ? cell?.type === "qr" : (cell?.type === "station" || cell?.type === "qr" || cell?.type === "charge")) && cell.label) pts.push({ r, c, label: cell.label });
            }
        }
        pts.sort((a, b) => a.label.localeCompare(b.label, undefined, { numeric: true }));
        return pts;
    }, [grid, qrOnly]);

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
                    if (next[r]?.[c]?.type !== "station" && next[r]?.[c]?.type !== "qr" && next[r]?.[c]?.type !== "charge") next[r]![c] = { type: "floor" };
                }
            } else if (pa.c === pb.c) {
                const c = pa.c;
                const r1 = Math.min(pa.r, pb.r);
                const r2 = Math.max(pa.r, pb.r);
                for (let r = r1; r <= r2; r++) {
                    if (next[r]?.[c]?.type !== "station" && next[r]?.[c]?.type !== "qr" && next[r]?.[c]?.type !== "charge") next[r]![c] = { type: "floor" };
                }
            } else {
                const pivot1 = { r: pa.r, c: pb.c };
                const pivot2 = { r: pb.r, c: pa.c };
                const pivots = [pivot1, pivot2];
                for (const pv of pivots) {
                    const carveRow = () => {
                        const r = pa.r;
                        const c1 = Math.min(pa.c, pv.c);
                        const c2 = Math.max(pa.c, pv.c);
                        for (let c = c1; c <= c2; c++) {
                            if (next[r]?.[c]?.type !== "station" && next[r]?.[c]?.type !== "qr" && next[r]?.[c]?.type !== "charge") next[r]![c] = { type: "floor" };
                        }
                    };
                    const carveCol = () => {
                        const c = pv.c;
                        const r1 = Math.min(pv.r, pb.r);
                        const r2 = Math.max(pv.r, pb.r);
                        for (let r = r1; r <= r2; r++) {
                            if (next[r]?.[c]?.type !== "station" && next[r]?.[c]?.type !== "qr" && next[r]?.[c]?.type !== "charge") next[r]![c] = { type: "floor" };
                        }
                    };
                    carveRow();
                    if (next[pv.r]?.[pv.c]?.type !== "station" && next[pv.r]?.[pv.c]?.type !== "qr" && next[pv.r]?.[pv.c]?.type !== "charge") next[pv.r]![pv.c] = { type: "floor" };
                    carveCol();
                    break;
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
            const key = path.map((p) => `${p.r},${p.c}`).join(";");
            const prev = anim.current.get(rb.id);
            const now = performance.now();
            if (prev && prev.key === key) {
                next.set(rb.id, { ...prev, last: now, color: rb.color });
            } else {
                next.set(rb.id, { key, points: pts, total, dist: 0, dir: 1, last: now, x: pts[0].x, y: pts[0].y, color: rb.color });
            }
        }
        anim.current = next;
    }, [robots, gridWithRoads, animateRobots]);

    useEffect(() => {
        if (!animateRobots) return;
        const speed = 120;
        const step = (now: number) => {
            const st = anim.current;
            for (const v of st.values()) {
                const dt = Math.min(0.1, Math.max(0, (now - v.last) / 1000));
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

    // Fit the QR network without changing coordinates used by navigation or telemetry.
    const viewport = useMemo(() => {
        if (!focusQrNetwork || stations.length === 0) return { x: 0, y: 0, width: GW, height: GH };
        let left = Math.min(...stations.map(p => p.c));
        let right = Math.max(...stations.map(p => p.c));
        let top = Math.min(...stations.map(p => p.r));
        let bottom = Math.max(...stations.map(p => p.r));
        for (const robot of robots) {
            // Include robots outside the QR area, but avoid zoom jitter from
            // normal motion within the padded network.
            if (Number.isFinite(robot.x) && (robot.x < left - .8 || robot.x > right + .8)) {
                left = Math.min(left, Math.floor(robot.x)); right = Math.max(right, Math.ceil(robot.x));
            }
            if (Number.isFinite(robot.y) && (robot.y < top - .8 || robot.y > bottom + .8)) {
                top = Math.min(top, Math.floor(robot.y)); bottom = Math.max(bottom, Math.ceil(robot.y));
            }
        }
        return { x: (left - .8) * CELL, y: (top - .8) * CELL,
            width: Math.max(4, right - left + 2.6) * CELL,
            height: Math.max(4, bottom - top + 2.6) * CELL };
    }, [focusQrNetwork, stations, robots]);
    const centerX = viewport.x + viewport.width / 2;
    const centerY = viewport.y + viewport.height / 2;
    const transform = `translate(${centerX + panOffset.x} ${centerY + panOffset.y}) scale(${scale}) translate(${-centerX} ${-centerY})`;

    // Selection changes do not need to rebuild the static map cells.
    const mapCells = useMemo(() => gridWithRoads.map((row, ri) => row.map((cell, ci) => {
        if (qrOnly && (cell.type === "station" || cell.type === "charge")) return null;
        const style = cellTypeStyles[cell.operationalRole === "CHARGING" ? "charge" : cell.type] || cellTypeStyles.floor;
        const x = ci * CELL;
        const y = ri * CELL;
        const station = cell.type === "qr" && cell.arucoId !== undefined ? stationByMarker.get(cell.arucoId) : undefined;
        const transit = directedLinks && cell.type === "qr" && cell.operationalRole === "NAVIGATION" && !cell.isIntersection;
        if (transit) return <g key={`${ri}-${ci}`}><title>{`QR${cell.arucoId} · lane checkpoint (no goods transfer)`}</title><rect x={x+16} y={y+16} width={8} height={8} rx={2} fill={isLight ? "#0e7490" : "#22d3ee"} /><text x={x+20} y={y+12} textAnchor="middle" fontSize={5} fill={isLight ? "#0e7490" : "#67e8f9"}>{cell.arucoId}</text></g>;
        const annotated = !compactLabels && !!(cell.displayName?.trim() || station || (cell.operationalRole && cell.operationalRole !== "NAVIGATION"));
        return (
            <g key={`${ri}-${ci}`} pointerEvents="none">
                {compactLabels && !(cell.displayName && cell.operationalRole !== "CHARGING") && <title>{[cell.arucoId != null ? `QR${cell.arucoId}` : cell.label,cell.displayName || station?.name,cell.operationalRole,station?.isOccupied ? "Occupied or reserved" : null].filter(Boolean).join(" · ")}</title>}
                <rect x={x} y={y} width={CELL} height={CELL} fill={cell.type === "floor" ? "transparent" : style.fill} stroke={style.stroke} strokeWidth="0.5" />
                {(cell.type === "station" || cell.type === "qr" || cell.type === "charge") && (
                    <>
                        <rect x={x + 2} y={y + 2} width={CELL - 4} height={CELL - 4} rx="3" fill={style.fill} stroke={style.stroke} strokeWidth="1.5" />
                        <text x={x + CELL / 2} y={y + (compactLabels && cell.operationalRole === "CHARGING" ? 16 : annotated ? 19 : CELL / 2 + 4)} textAnchor="middle" fontSize={compactLabels ? 14 : qrOnly ? 11 : 8} fontFamily="monospace" fontWeight="bold" fill={style.stroke}>
                            {cell.type === "qr" && cell.arucoId !== undefined ? `QR${cell.arucoId}` : cell.label}
                        </text>
                        {compactLabels && cell.operationalRole === "CHARGING" && <text x={x+CELL/2} y={y+31} textAnchor="middle" fontSize={12} fontWeight="bold" fill={style.stroke}>{station?.code??"CHG"}</text>}
                        {cell.type === "qr" && cell.isIntersection && (
                            <g>
                                <circle cx={x + CELL - 8} cy={y + 8} r={6} fill="#f59e0b" />
                                <text x={x + CELL - 8} y={y + 11} textAnchor="middle" fontSize="8" fontWeight="bold" fill="#111827">I</text>
                            </g>
                        )}
                    </>
                )}
            </g>
        );
    })), [gridWithRoads, cellTypeStyles, qrOnly, stationByMarker,directedLinks,isLight,compactLabels]);

    const stationLabels = stations.map(({ r, c, label }) => {
        const cell = grid[r]?.[c];
        const station = cell?.type === "qr" && cell.arucoId !== undefined ? stationByMarker.get(cell.arucoId) : undefined;
        if (cell?.type !== "qr") return null;
        const fullName = cell.displayName?.trim() || station?.name;
        if(compactLabels && (!cell.displayName || cell.operationalRole === "CHARGING")) return null;
        const role = cell.isIntersection ? "Intersection" : cell.operationalRole ?? station?.type;
        const roleLabel = role && (stationRoles[role as keyof typeof stationRoles] ?? (role === "NAVIGATION" ? "Navigation" : role));
        if (!fullName && (!roleLabel || roleLabel === "Navigation" || roleLabel === "Intersection")) return null;
        const description = station && !station.isActive ? `${roleLabel ?? ""} (inactive)` : roleLabel;
        const primary = fullName || description || "";
        const name = primary.length > 19 ? `${primary.slice(0, 18)}…` : primary;
        const detail = fullName && description;
        const color = isLight ? "#4c1d95" : "#f3e8ff";
        const x = c * CELL + CELL / 2;
        const y = r * CELL;
        const badgeWidth = CELL * 2 - 4;
        return <g key={label} pointerEvents="none" aria-label={[fullName, description].filter(Boolean).join(" — ")}>
            <title>{[station?.code, fullName, description].filter(Boolean).join(" · ")}</title>
            <rect x={x - badgeWidth / 2} y={y + 23} width={badgeWidth} height={detail ? 23 : 15} rx="3" fill={cellTypeStyles.qr.fill} stroke={cellTypeStyles.qr.stroke} strokeWidth="0.6" />
            <text x={x} y={y + 33} textAnchor="middle" fontSize="9" fontWeight="bold" fill={color}
                textLength={name.length > 13 ? badgeWidth - 8 : undefined} lengthAdjust="spacingAndGlyphs">{name}</text>
            {detail && <text x={x} y={y + 43} textAnchor="middle" fontSize="8" fill={color}
                textLength={detail.length > 16 ? badgeWidth - 8 : undefined} lengthAdjust="spacingAndGlyphs">{detail}</text>}
        </g>;
    });

    return (
        <svg aria-label={focusQrNetwork && !onCellClick && !onPointClick ? "Live QR robot map" : "Warehouse map"} width="100%" height="100%" viewBox={`${viewport.x} ${viewport.y} ${viewport.width} ${viewport.height}`} style={{ background: isLight ? "#ffffff" : "#060d1f", display: "block", transition: "background 0.2s ease" }} preserveAspectRatio="xMidYMid meet">
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
                <marker id="arrow" markerWidth="4" markerHeight="4" refX="2" refY="2" orient="auto-start-reverse">
                    <path d="M0,0 L4,2 L0,4 Z" fill="#22d3ee" opacity="0.5" />
                </marker>
            </defs>

            <g transform={transform}>
                <rect width={GW} height={GH} fill="url(#dots)" />

                {linksToDraw.map(([a, b]) => {
                    const pa = stations.find((s) => s.label === a);
                    const pb = stations.find((s) => s.label === b);
                    if (!pa || !pb) return null;
                    const x1 = pa.c * CELL + CELL / 2, y1 = pa.r * CELL + CELL / 2;
                    const x2 = pb.c * CELL + CELL / 2, y2 = pb.r * CELL + CELL / 2;
                    const horizontal = Math.abs(x2 - x1), vertical = Math.abs(y2 - y1);
                    const length = horizontal + vertical;
                    if (!length) return null;
                    // A small chevron sits halfway along the road, outside QR nodes.
                    // Opposite directions on a shared bay spur get separate offsets.
                    const reverse = directedLinks && linksToDraw.some(([from, to]) => from === b && to === a);
                    const mid = length / 2;
                    const dx = mid <= horizontal ? Math.sign(x2 - x1) : 0;
                    const dy = mid > horizontal ? Math.sign(y2 - y1) : 0;
                    const mx = mid <= horizontal ? x1 + dx * mid : x2;
                    const my = mid <= horizontal ? y1 : y1 + dy * (mid - horizontal);
                    const angle = Math.atan2(dy, dx) * 180 / Math.PI;
                    const arrow = (opposite: boolean) => {
                        const direction = opposite ? -1 : 1;
                        const offset = reverse || !directedLinks ? 3 * direction : 0;
                        return <path key={String(opposite)} data-lane-direction={`${opposite ? b : a} to ${opposite ? a : b}`}
                            d="M-3,-2.5 L1,0 L-3,2.5" fill="none"
                            stroke={isLight ? "#0891b2" : "#67c6d8"} strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round"
                            opacity="0.75" pointerEvents="none"
                            transform={`translate(${mx - dy * offset} ${my + dx * offset}) rotate(${angle + (opposite ? 180 : 0)})`} />;
                    };
                    return (
                        <g key={`${a}::${b}`}>
                        {(!reverse || a.localeCompare(b) < 0) && <polyline
                            points={`${pa.c * CELL + CELL / 2},${pa.r * CELL + CELL / 2} ${pb.c * CELL + CELL / 2},${pa.r * CELL + CELL / 2} ${pb.c * CELL + CELL / 2},${pb.r * CELL + CELL / 2}`}
                            fill="none"
                            stroke={isLight ? "rgba(6,182,212,0.35)" : "rgba(34,211,238,0.25)"}
                            strokeWidth="6"
                            strokeLinecap="round"
                        />}
                        {showDirections && arrow(false)}
                        {showDirections && !directedLinks && arrow(true)}
                        </g>
                    );
                })}

                {mapCells}
                {(onCellClick || handlePointClick || selectedCell || selectedPoint) && gridWithRoads.map((row, ri) => row.map((cell, ci) => {
                    if (qrOnly && (cell.type === "station" || cell.type === "charge")) return null;
                    const x = ci * CELL; const y = ri * CELL;
                    const isPoint = cell.type === "station" || cell.type === "qr" || cell.type === "charge";
                    const isInteractive = !!onCellClick || (!!handlePointClick && isPoint);
                    const pointName = qrOnly && cell.type === "qr" && cell.arucoId !== undefined
                        ? `QR${cell.arucoId}` : `${cell.type} ${cell.label ?? ""}`;
                    const isSelected = (selectedCell?.row === ri && selectedCell.col === ci)
                        || (!!selectedPoint && cell.label === selectedPoint);
                    const selectCell = () => {
                        if (isPoint && cell.label && handlePointClick) handlePointClick(cell.label);
                        if (onCellClick) onCellClick(ri, ci);
                    };
                    return (
                        <g key={`${ri}-${ci}`}>
                            {isSelected && (
                                <rect
                                    x={x + 1} y={y + 1} width={CELL - 2} height={CELL - 2} rx="5"
                                    fill="rgba(34,211,238,0.18)" stroke="#22d3ee" strokeWidth="2.5"
                                    pointerEvents="none"
                                />
                            )}
                            {isInteractive && (
                                <rect
                                    x={x}
                                    y={y}
                                    width={CELL}
                                    height={CELL}
                                    fill="transparent"
                                    role={isPoint ? "button" : undefined}
                                    tabIndex={isPoint ? 0 : undefined}
                                    aria-label={isPoint ? `Select ${pointName}` : undefined}
                                    aria-pressed={isPoint ? isSelected : undefined}
                                    data-map-cell={`${ri},${ci}`}
                                    onClick={selectCell}
                                    onKeyDown={(event) => {
                                        if (event.key === "Enter" || event.key === " ") {
                                            event.preventDefault();
                                            selectCell();
                                        }
                                    }}
                                    className="hover:fill-cyan-400/15 active:fill-cyan-400/25 focus-visible:stroke-cyan-400 focus-visible:stroke-2 focus-visible:outline-none transition-none"
                                    style={{ cursor: "pointer" }}
                                />
                            )}
                        </g>
                    );
                }))}
                {stationLabels}

            {missionRoutes.filter(route => route.points.length > 1).map(route => <polyline key={`mission-${route.robotId}`}
                points={route.points.map(p => `${p.x * CELL + CELL / 2},${p.y * CELL + CELL / 2}`).join(" ")}
                fill="none" stroke={route.color} strokeWidth="2" strokeDasharray="6 4" opacity="0.65" />)}
            {robots
                .filter((rb) => rb.missionId && rb.mqtt === "ONLINE")
                .map((rb) => {
                    const sr = clampInt(rb.y, 0, GROWS - 1);
                    const sc = clampInt(rb.x, 0, GCOLS - 1);
                    const gr = clampInt(rb.ty, 0, GROWS - 1);
                    const gc = clampInt(rb.tx, 0, GCOLS - 1);
                    const path = findPath(gridWithRoads, { r: sr, c: sc }, { r: gr, c: gc });
                    if (!path || path.length < 2) return null;
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
                    <g key={rb.id} filter={`url(#${filterId})`} onClick={() => onRobotClick?.(rb.id)}
                        style={{ cursor: onRobotClick ? "pointer" : "default" }} data-robot-code={rb.id}>
                        <title>{rb.id} · {rb.positionLabel ?? "Robot position"}</title>
                        {rb.mqtt === "ONLINE" && (
                            <circle cx={cx} cy={cy} r={R + 5} fill="none" stroke={rb.color} strokeWidth="0.8" opacity="0.3">
                                <animate attributeName="r" values={`${R + 3};${R + 10};${R + 3}`} dur="2s" repeatCount="indefinite" />
                                <animate attributeName="opacity" values="0.4;0;0.4" dur="2s" repeatCount="indefinite" />
                            </circle>
                        )}
                        {rb.heading !== undefined && <path d="M0 -18 L-4 -12 L4 -12 Z" fill={rb.color}
                            transform={`translate(${cx},${cy}) rotate(${-rb.heading})`} />}
                        <rect x={cx - R} y={cy - R} width={R * 2} height={R * 2} rx="4"
                            style={{ transition: animateRobots ? "none" : "x 100ms linear, y 100ms linear" }}
                            fill={rb.mqtt === "OFFLINE" ? "#1a0a0a" : (isLight ? "#ffffff" : "#0a1628")}
                            stroke={rb.color} strokeWidth="1.5" />
                        <text x={cx} y={cy + 4} textAnchor="middle" fontSize="12" fill={rb.color}>⬡</text>
                        <text x={cx} y={cy + R + 10} textAnchor="middle" fontSize={focusQrNetwork ? 9 : 7} fontFamily="monospace" fontWeight="600" fill={rb.color} opacity="0.9">{rb.id.replace("AMR-", "")}</text>
                        <circle cx={cx + R - 3} cy={cy - R + 3} r="3" fill={rb.battery <= 15 ? "#ef4444" : rb.battery <= 35 ? "#f59e0b" : "#22c55e"} />
                    </g>
                );
            })}
            </g>
        </svg>
    );
}
