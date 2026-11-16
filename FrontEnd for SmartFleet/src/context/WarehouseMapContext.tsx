import { createContext, useContext, useState, type Dispatch, type SetStateAction, type ReactNode } from "react"
import { createDefaultWarehouseLayout, type WarehouseLayout } from "../components/WarehouseGrid"

interface WarehouseMapState {
  warehouseLayout: WarehouseLayout
  setWarehouseLayout: Dispatch<SetStateAction<WarehouseLayout>>
  links: Array<[string, string]>
  setLinks: Dispatch<SetStateAction<Array<[string, string]>>>
}

export function computeDefaultLinks(layout: WarehouseLayout): Array<[string, string]> {
  const rows = new Map<number, Array<{ label: string; c: number }>>()
  const cols = new Map<number, Array<{ label: string; r: number }>>()
  layout.forEach((row, r) => row.forEach((cell, c) => {
    if (!cell.label || !["station", "qr", "charge"].includes(cell.type)) return
    rows.set(r, [...(rows.get(r) ?? []), { label: cell.label, c }])
    cols.set(c, [...(cols.get(c) ?? []), { label: cell.label, r }])
  }))
  const links: Array<[string, string]> = []
  for (const row of rows.values()) {
    row.sort((a, b) => a.c - b.c)
    for (let i = 1; i < row.length; i++) links.push([row[i - 1].label, row[i].label])
  }
  for (const col of cols.values()) {
    col.sort((a, b) => a.r - b.r)
    for (let i = 1; i < col.length; i++) links.push([col[i - 1].label, col[i].label])
  }
  return links
}

const WarehouseMapContext = createContext<WarehouseMapState | null>(null)

export default function WarehouseMapProvider({ children }: { children: ReactNode }) {
  const [warehouseLayout, setWarehouseLayout] = useState(createDefaultWarehouseLayout)
  const [links, setLinks] = useState(() => computeDefaultLinks(warehouseLayout))
  return (
    <WarehouseMapContext.Provider value={{ warehouseLayout, setWarehouseLayout, links, setLinks }}>
      {children}
    </WarehouseMapContext.Provider>
  )
}

export function useWarehouseMap() {
  const context = useContext(WarehouseMapContext)
  if (!context) throw new Error("useWarehouseMap must be used within WarehouseMapProvider")
  return context
}
