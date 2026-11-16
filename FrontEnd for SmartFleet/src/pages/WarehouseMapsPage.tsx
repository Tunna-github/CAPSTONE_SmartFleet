import { useState, useCallback, useEffect, useMemo } from "react"

import { useWarehouseMap, computeDefaultLinks } from "../context/WarehouseMapContext"
import { Icon, IC } from "../components/Icons"
import { PageHeader, PrimaryBtn } from "../components/SharedUI"
import {
  WarehouseGrid,
  createDefaultWarehouseLayout,
} from "../components/WarehouseGrid"

export default function WarehouseMapsPage() {
  const { warehouseLayout, setWarehouseLayout, links, setLinks } = useWarehouseMap()
  const [toast, setToast] = useState<{
    msg: string
    type: "success" | "error" | "info"
  } | null>(null)
  const [warehouseScale, setWarehouseScale] = useState(1)
  const [pendingStation, setPendingStation] = useState<string | null>(null)
  const [selectedCell, setSelectedCell] = useState<{ row: number; col: number } | null>(null)
  const [mapMode, setMapMode] = useState<"links" | "points">("links")
  const [brush, setBrush] =
    useState<"wall" | "floor" | "station" | "qr" | "charge">("qr")
  const [brushLabel, setBrushLabel] = useState("")

  const stationLabels = useMemo(() => {
    const out: string[] = []
    for (let r = 0; r < warehouseLayout.length; r++) {
      for (let c = 0; c < warehouseLayout[r]!.length; c++) {
        const cell = warehouseLayout[r]![c]!
        if (
          (cell.type === "station" ||
            cell.type === "qr" ||
            cell.type === "charge") &&
          cell.label
        )
          out.push(cell.label)
      }
    }
    out.sort((a, b) => a.localeCompare(b, undefined, { numeric: true }))
    return out
  }, [warehouseLayout])

  const normalizeLink = useCallback(
    (a: string, b: string): [string, string] => {
      return a.localeCompare(b, undefined, { numeric: true }) <= 0
        ? [a, b]
        : [b, a]
    },
    [],
  )

  const toggleLink = useCallback(
    (a: string, b: string) => {
      const [x, y] = normalizeLink(a, b)
      const key = `${x}::${y}`
      setLinks((prev) => {
        const exists = prev.some(([p, q]) => `${p}::${q}` === key)
        if (exists) return prev.filter(([p, q]) => `${p}::${q}` !== key)
        return [...prev, [x, y]]
      })
    },
    [normalizeLink],
  )

  const handleStationClick = useCallback(
    (label: string) => {
      if (!pendingStation) {
        setPendingStation(label)
        return
      }
      if (pendingStation === label) {
        setPendingStation(null)
        return
      }
      toggleLink(pendingStation, label)
      setPendingStation(null)
    },
    [pendingStation, toggleLink],
  )

  const zoomOut = useCallback(
    () =>
      setWarehouseScale((s) => Math.max(0.5, Math.round((s - 0.1) * 10) / 10)),
    [],
  )
  const zoomIn = useCallback(
    () =>
      setWarehouseScale((s) => Math.min(2.5, Math.round((s + 0.1) * 10) / 10)),
    [],
  )
  const zoomReset = useCallback(() => setWarehouseScale(1), [])

  const nextCode = useCallback(
    (prefix: string) => {
      const nums = stationLabels
        .map((s) => {
          if (!s.toUpperCase().startsWith(prefix)) return null
          const n = Number.parseInt(s.slice(prefix.length), 10)
          return Number.isFinite(n) ? n : null
        })
        .filter((x): x is number => x != null)
      const next = (nums.length ? Math.max(...nums) : 0) + 1
      return prefix === "QR"
        ? `${prefix}${String(next).padStart(2, "0")}`
        : `${prefix}${next}`
    },
    [stationLabels],
  )

  const paintCell = useCallback(
    (row: number, col: number) => {
      const placingPoint =
        brush === "station" || brush === "qr" || brush === "charge"
      const isSelected = selectedCell?.row === row && selectedCell.col === col
      if (isSelected && placingPoint) {
        setSelectedCell(null)
        setToast(null)
        return
      }
      setSelectedCell(isSelected ? null : { row, col })
      const occupiedCell = warehouseLayout[row]?.[col]
      if (
        placingPoint &&
        occupiedCell &&
        (occupiedCell.type === "station" ||
          occupiedCell.type === "qr" ||
          occupiedCell.type === "charge")
      ) {
        const itemName =
          occupiedCell.type === "station"
            ? "station"
            : occupiedCell.type === "qr"
              ? "QR"
              : "charging station"
        setToast({
          msg: `This cell already contains ${itemName} ${occupiedCell.label ?? ""}. Items cannot overlap. Choose another cell or remove the existing item using WALL/FLOOR first.`,
          type: "error",
        })
        return
      }

      setWarehouseLayout((prev) => {
        const next = prev.map((r) => r.map((c) => ({ ...c })))
        const cell = next[row]?.[col]
        if (!cell) return prev

        const oldLabel = cell.label
        const oldIsPoint =
          cell.type === "station" ||
          cell.type === "qr" ||
          cell.type === "charge"
        const newIsPoint =
          brush === "station" || brush === "qr" || brush === "charge"
        const labelInput = brushLabel.trim()

        if (brush === "wall" || brush === "floor") {
          next[row][col] = { type: brush }
          if (oldIsPoint && oldLabel) {
            setLinks((prevLinks) =>
              prevLinks.filter(([a, b]) => a !== oldLabel && b !== oldLabel),
            )
            if (pendingStation === oldLabel) setPendingStation(null)
          }
          return next
        }

        let newLabel = labelInput
        if (!newLabel) {
          if (brush === "qr") newLabel = nextCode("QR")
          else if (brush === "charge") newLabel = nextCode("CHG")
          else newLabel = nextCode("S")
        }

        next[row][col] = { type: brush, label: newLabel }
        if (oldIsPoint && oldLabel && oldLabel !== newLabel) {
          setLinks((prevLinks) =>
            prevLinks.map(([a, b]) => [
              a === oldLabel ? newLabel : a,
              b === oldLabel ? newLabel : b,
            ]),
          )
          if (pendingStation === oldLabel) setPendingStation(newLabel)
        }
        if (!oldIsPoint && newIsPoint) {
          setLinks((prevLinks) => prevLinks)
        }
        return next
      })
    },
    [brush, brushLabel, nextCode, pendingStation, warehouseLayout, selectedCell],
  )

  return (
    <div
      className="flex flex-col h-full min-h-0 overflow-hidden"
      style={{ background: "var(--background)" }}
    >
      <PageHeader
        title="Warehouse Maps"
        sub="Edit routes, stations, charging points and QR markers"
      >
        <PrimaryBtn
          onClick={() => {
            const next = createDefaultWarehouseLayout()
            setWarehouseLayout(next)
            setLinks(computeDefaultLinks(next))
            setPendingStation(null)
            setSelectedCell(null)
            setWarehouseScale(1)
          }}
        >
          <Icon d={IC.refresh} size={15} /> Reset Map
        </PrimaryBtn>
      </PageHeader>
      <div
        className="flex-1 min-h-0 overflow-y-auto p-6"
        role="region"
        aria-label="Warehouse map editor"
        tabIndex={0}
      >
        <div
          className="rounded-2xl border overflow-hidden"
          style={{
            borderColor: "var(--border-subtle)",
            background: "var(--surface-2)",
          }}
        >
          <div className="flex flex-col">
            <div
              className="shrink-0 flex flex-wrap items-center gap-3 px-4 py-3"
              style={{
                borderBottom: "1px solid var(--border-subtle)",
                background: "var(--surface-1)",
              }}
            >
              <div className="flex items-center gap-2">
                <button
                  type="button"
                  onClick={() => {
                    setMapMode("links")
                    setBrushLabel("")
                    setSelectedCell(null)
                  }}
                  className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                  style={{
                    background:
                      mapMode === "links"
                        ? "rgba(34,211,238,0.14)"
                        : "var(--surface-3)",
                    borderColor:
                      mapMode === "links"
                        ? "rgba(34,211,238,0.30)"
                        : "var(--border-medium)",
                    color:
                      mapMode === "links" ? "#22d3ee" : "var(--text-primary)",
                  }}
                >
                  Edit Links
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setMapMode("points")
                    setPendingStation(null)
                  }}
                  className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                  style={{
                    background:
                      mapMode === "points"
                        ? "rgba(34,211,238,0.14)"
                        : "var(--surface-3)",
                    borderColor:
                      mapMode === "points"
                        ? "rgba(34,211,238,0.30)"
                        : "var(--border-medium)",
                    color:
                      mapMode === "points" ? "#22d3ee" : "var(--text-primary)",
                  }}
                >
                  Edit Points
                </button>
              </div>

              {mapMode === "links" && (
                <span
                  className="text-[11px] font-mono"
                  style={{ color: "var(--text-faint)" }}
                >
                  Click 2 points to toggle a bidirectional link. Selected:{" "}
                  {pendingStation ?? "—"}
                </span>
              )}

              {mapMode === "points" && (
                <div className="flex flex-wrap items-center gap-2">
                  <select
                    value={brush}
                    onChange={(e) => setBrush(e.target.value as typeof brush)}
                    className="px-2.5 py-2 rounded-lg text-[12px] outline-none"
                    style={{
                      background: "var(--surface-3)",
                      border: "1px solid var(--border-medium)",
                      color: "var(--text-primary)",
                    }}
                    aria-label="Brush type"
                  >
                    {([
                      "station",
                      "qr",
                      "charge",
                      "floor",
                      "wall",
                    ] as const).map((t) => (
                      <option key={t} value={t}>
                        {t.toUpperCase()}
                      </option>
                    ))}
                  </select>
                  <input
                    value={brushLabel}
                    onChange={(e) => setBrushLabel(e.target.value)}
                    placeholder={
                      brush === "qr"
                        ? "QR01 (optional)"
                        : brush === "charge"
                          ? "CHG1 (optional)"
                          : "S1 (optional)"
                    }
                    className="px-3 py-2 rounded-lg text-[12px] outline-none w-[170px]"
                    style={{
                      background: "var(--surface-3)",
                      border: "1px solid var(--border-medium)",
                      color: "var(--text-primary)",
                    }}
                    aria-label="Point label"
                  />
                  <span
                    className="text-[11px] font-mono"
                    style={{ color: "var(--text-dimmest)" }}
                  >
                    Click a cell to paint
                  </span>
                </div>
              )}

              <div className="ml-auto flex items-center gap-2">
                <button
                  type="button"
                  onClick={zoomOut}
                  className="px-3 py-2 rounded-lg border text-[12px]"
                  style={{
                    background: "var(--surface-3)",
                    borderColor: "var(--border-medium)",
                    color: "var(--text-primary)",
                  }}
                >
                  −
                </button>
                <button
                  type="button"
                  onClick={zoomReset}
                  className="px-3 py-2 rounded-lg border text-[12px]"
                  style={{
                    background: "var(--surface-3)",
                    borderColor: "var(--border-medium)",
                    color: "var(--text-primary)",
                  }}
                >
                  {Math.round(warehouseScale * 100)}%
                </button>
                <button
                  type="button"
                  onClick={zoomIn}
                  className="px-3 py-2 rounded-lg border text-[12px]"
                  style={{
                    background: "var(--surface-3)",
                    borderColor: "var(--border-medium)",
                    color: "var(--text-primary)",
                  }}
                >
                  +
                </button>
              </div>
            </div>
            <div
              className="overflow-auto"
              role="region"
              aria-label="Scrollable warehouse map"
              tabIndex={0}
              style={{ background: "var(--surface-3)", height: 520 }}
            >
              <div
                style={{
                  width: `${warehouseScale * 100}%`,
                  minWidth: 800 * warehouseScale,
                  height: 720 * warehouseScale,
                }}
              >
                <WarehouseGrid
                  robots={[]}
                  animateRobots={false}
                  grid={warehouseLayout}
                  links={links}
                  scale={1}
                  selectedCell={mapMode === "points" ? selectedCell : null}
                  selectedPoint={pendingStation}
                  onCellClick={mapMode === "points" ? paintCell : undefined}
                  onStationClick={
                    mapMode === "links" ? handleStationClick : undefined
                  }
                />
              </div>
            </div>
            <div
              className="shrink-0 px-4 py-3"
              style={{
                borderTop: "1px solid var(--border-subtle)",
                background: "var(--surface-1)",
              }}
            >
              <div className="flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={() => {
                    setLinks([])
                    setPendingStation(null)
                  }}
                  className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                  style={{
                    background: "rgba(239,68,68,0.10)",
                    borderColor: "rgba(239,68,68,0.25)",
                    color: "#f87171",
                  }}
                >
                  Clear Links
                </button>
                <button
                  type="button"
                  onClick={() => {
                    setLinks(computeDefaultLinks(warehouseLayout))
                    setPendingStation(null)
                  }}
                  className="px-3 py-2 rounded-lg border text-[12px] font-semibold"
                  style={{
                    background: "var(--surface-3)",
                    borderColor: "var(--border-medium)",
                    color: "var(--text-primary)",
                  }}
                >
                  Reset Links
                </button>
                <span
                  className="ml-auto text-[11px] font-mono"
                  style={{ color: "var(--text-dimmest)" }}
                >
                  Links: {links.length} · Points: {stationLabels.length}
                </span>
                <span role="status" className="text-[11px] font-mono" style={{ color: "#22d3ee" }}>
                  Selected: {mapMode === "links"
                    ? pendingStation ?? "None"
                    : selectedCell
                      ? warehouseLayout[selectedCell.row]?.[selectedCell.col]?.label ?? `Cell (${selectedCell.col}, ${selectedCell.row})`
                      : "None"}
                </span>
              </div>
              {links.length > 0 && (
                <div className="mt-3 flex flex-wrap gap-2">
                  {links.map(([a, b]) => {
                    const key = `${a}::${b}`
                    return (
                      <button
                        key={key}
                        type="button"
                        onClick={() => toggleLink(a, b)}
                        className="px-2.5 py-1 rounded-md text-[10px] font-mono font-bold border transition-colors"
                        style={{
                          background: "rgba(6,182,212,0.10)",
                          borderColor: "rgba(6,182,212,0.25)",
                          color: "#22d3ee",
                        }}
                        aria-label={`Remove link ${a} to ${b}`}
                      >
                        {a} ↔ {b}
                      </button>
                    )
                  })}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
      {toast && (
        <ToastEl
          msg={toast.msg}
          type={toast.type}
          onDone={() => setToast(null)}
        />
      )}
    </div>
  )
}
function ToastEl({ msg, type, onDone }: any) {
  useEffect(() => {
    const t = setTimeout(() => {
      onDone()
    }, 3000)
    return () => clearTimeout(t)
  }, [onDone])
  return (
    <div
      role={type === "error" ? "alert" : "status"}
      className="fixed bottom-6 right-6 z-[200] px-4 py-3 rounded-xl border border-gray-700 shadow-2xl"
      style={{
        background: "var(--surface-2)",
        color: type === "error" ? "#f87171" : "var(--text-primary)",
        maxWidth: "min(420px, calc(100vw - 48px))",
      }}
    >
      {msg}
    </div>
  )
}
