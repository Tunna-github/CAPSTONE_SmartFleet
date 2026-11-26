import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { apiClient } from "../api/client";
import { stationRoles, useCatalog } from "../api/modules/catalog";
import { getMapReadiness, type CheckpointRole, type MapReadiness } from "../api/modules/navigation";
import CheckpointStationEditor from "../components/CheckpointStationEditor";
import { PageHeader } from "../components/SharedUI";
import { WarehouseGrid, type WarehouseCell } from "../components/WarehouseGrid";
import { useWarehouseMap } from "../context/WarehouseMapContext";

const control = "rounded-lg border border-[var(--border-medium)] bg-[var(--surface-3)] px-3 py-2 text-sm disabled:opacity-50";
const field = "grid gap-1 text-xs";
const isSameLabel = (a: string, b: string) => a.toLowerCase() === b.toLowerCase();

export default function WarehouseMapsPage() {
  const { catalog, error: catalogError, reload: reloadCatalog } = useCatalog();
  const {
    warehouseLayout, setWarehouseLayout, links, setLinks,
    mapRevision, mapDirty, mapLoading, mapSaving, mapError, reloadMap, saveMap,
  } = useWarehouseMap();
  const [mode, setMode] = useState<"points" | "links">("points");
  const [adding, setAdding] = useState(false);
  const [surface, setSurface] = useState<"floor" | "wall" | "">("");
  const [addId, setAddId] = useState("");
  const [selected, setSelected] = useState<{ row: number; col: number } | null>(null);
  const [pendingLink, setPendingLink] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ message: string; error?: boolean } | null>(null);
  const [scale, setScale] = useState(1);
  const [readiness, setReadiness] = useState<MapReadiness | null>(null);
  const [simulation, setSimulation] = useState<{ enabled: boolean; mapCompatible: boolean } | null>(null);
  const [statusError, setStatusError] = useState("");

  useEffect(() => {
    if (mapRevision === null || mapLoading || mapSaving) return;
    let cancelled = false;
    setSimulation(null);
    setReadiness(null);
    setStatusError("");
    void getMapReadiness().then(value => {
      if (!cancelled) setReadiness(value);
    }).catch(() => {
      if (!cancelled) setStatusError("Could not check route readiness.");
    });
    void apiClient.get<{ enabled: boolean; mapCompatible: boolean }>("/api/v1/simulation").then(({ data }) => {
      if (!cancelled) setSimulation(data);
    }).catch(() => {
      if (!cancelled) setStatusError("Could not check Webots compatibility. Reload Backend Map to retry.");
    });
    return () => { cancelled = true; };
  }, [mapRevision, mapLoading, mapSaving]);

  const cell = selected ? warehouseLayout[selected.row]?.[selected.col] : undefined;
  const checkpoint = cell?.type === "qr" ? cell : undefined;
  const qrCells = warehouseLayout.flat().filter(point => point.type === "qr");
  const qrKeys = new Set(qrCells.map(point => point.label?.toLowerCase()));
  const qrLinks = links.filter(([a, b]) => qrKeys.has(a.toLowerCase()) && qrKeys.has(b.toLowerCase()));
  const mappedStation = catalog?.stations.find(station =>
    station.warehouseId === catalog.navigationWarehouseId && station.arucoId === checkpoint?.arucoId);
  const busy = mapLoading || mapSaving;
  const canManageStation = !mapDirty && !busy && catalog?.mapRevision === mapRevision;

  function updateCell(row: number, col: number, update: (previous: WarehouseCell) => WarehouseCell) {
    setWarehouseLayout(previous => previous.map((cells, r) =>
      r === row ? cells.map((point, c) => c === col ? update(point) : point) : cells));
  }

  function editCheckpoint(changes: Partial<WarehouseCell>) {
    if (!selected || !checkpoint || busy) return;
    updateCell(selected.row, selected.col, previous => ({ ...previous, ...changes }));
  }

  function handleCellClick(row: number, col: number) {
    if (busy) return;
    const current = warehouseLayout[row]?.[col];
    if (!current || current.type === "station" || current.type === "charge") return;
    if (current.type === "qr") {
      setSelected({ row, col });
      return;
    }
    if (surface && current.type !== surface && mapRevision !== null) {
      updateCell(row, col, () => ({ type: surface }));
      return;
    }
    if (!adding || mapRevision === null) return;
    const used = new Set(qrCells.map(point => point.arucoId));
    const id = addId.trim() === "" ? Array.from({ length: 50 }, (_, i) => i).find(i => !used.has(i)) : Number(addId);
    if (id === undefined || !Number.isInteger(id) || id < 0 || id > 49 || used.has(id)) {
      setNotice({ message: "Choose an unused ArUco ID from 0 to 49.", error: true });
      return;
    }
    const stem = `QR${String(id).padStart(2, "0")}`;
    const labels = new Set(warehouseLayout.flat().map(point => point.label?.toLowerCase()));
    let label = stem;
    for (let suffix = 2; labels.has(label.toLowerCase()); suffix++) label = `${stem}-${suffix}`;
    updateCell(row, col, () => ({ type: "qr", label, arucoId: id }));
    setSelected({ row, col });
    setAdding(false);
    setAddId("");
    setNotice(null);
  }

  function removeCheckpoint() {
    if (!selected || !checkpoint?.label || busy) return;
    const label = checkpoint.label;
    updateCell(selected.row, selected.col, () => ({ type: "floor" }));
    setLinks(previous => previous.filter(([from, to]) => !isSameLabel(from, label) && !isSameLabel(to, label)));
    setSelected(null);
  }

  function toggleLink(a: string, b: string) {
    if (busy) return;
    const matches = ([from, to]: [string, string]) =>
      (isSameLabel(from, a) && isSameLabel(to, b)) || (isSameLabel(from, b) && isSameLabel(to, a));
    setLinks(previous => previous.some(matches) ? previous.filter(link => !matches(link)) : [...previous, [a, b]]);
  }

  function selectLinkEndpoint(label: string) {
    if (busy) return;
    if (!pendingLink) {
      setPendingLink(label);
      return;
    }
    if (!isSameLabel(pendingLink, label)) toggleLink(pendingLink, label);
    setPendingLink(null);
  }

  async function syncMap(save: boolean) {
    setNotice(null);
    try {
      await (save ? saveMap() : reloadMap());
      await reloadCatalog();
      if (!save) {
        setSelected(null);
        setPendingLink(null);
        setAdding(false);
        setSurface("");
      }
      setNotice({ message: save ? "Saved map to database!" : "Reloaded map from Backend." });
    } catch (error) {
      setNotice({ message: `${save ? "Save" : "Reload"} failed: ${(error as { message?: string }).message ?? "API error"}`, error: true });
    }
  }

  function changeMode(next: typeof mode) {
    setMode(next);
    setAdding(false);
    setSurface("");
    setPendingLink(null);
  }

  const checkpointRole = checkpoint?.isIntersection ? "INTERSECTION" :
    checkpoint?.operationalRole ?? mappedStation?.type ?? "NAVIGATION";

  return <main className="flex h-full min-h-0 flex-col overflow-hidden text-[var(--text-primary)]">
    <PageHeader title="Warehouse Maps" sub="Edit QR checkpoints, corridor links and station assignments">
      <Link className={control} to="/warehouse-stations">Advanced warehouse administration</Link>
      <button className={control} type="button" disabled={busy} onClick={() => void syncMap(false)}>Reload Backend Map</button>
      <button className={`${control} !bg-teal-700 !text-white`} type="button" disabled={busy || mapRevision === null}
        onClick={() => void syncMap(true)}>{mapSaving ? "Saving…" : "Save to Database"}</button>
    </PageHeader>
    <div className="flex-1 space-y-3 overflow-auto px-6 py-4">
      <p className="text-xs text-[var(--text-faint)]">Saved map revision: {mapRevision ?? "not loaded"}{mapDirty && " · Unsaved map edits"}</p>
      {simulation?.enabled && !simulation.mapCompatible && <p role="alert" className="rounded-lg border border-amber-500 p-3 text-sm">
        Simulation dispatch is blocked. The saved QR map differs from the commissioned Webots world. Restore the commissioned design or recommission the world before dispatching.
      </p>}
      {simulation?.enabled && mapDirty && <p className="text-xs">Webots dispatch uses the saved map. Compatibility is checked after saving.</p>}
      {readiness && <p role="status" className="text-xs">Saved QR map: {readiness.qrNodeCount} markers · {readiness.corridorCount} corridors.
        {readiness.readyForRouting ? " Connected for route planning." : ` Not ready for QR tasks: ${readiness.issues.join(" ")}`}</p>}
      {mapError && <p role="alert" className="text-sm">Navigation API: {mapError}</p>}
      {catalogError && <p role="alert" className="text-sm">Station catalog: {catalogError} <button className="underline" onClick={() => void reloadCatalog()}>Reload catalog</button></p>}
      {statusError && <p role="alert" className="text-sm">{statusError}</p>}
      {notice && <p role={notice.error ? "alert" : "status"} className="text-sm">{notice.message}</p>}

      <section aria-label="Warehouse map editor" className="overflow-hidden rounded-xl border border-[var(--border-subtle)] bg-[var(--surface-2)]">
        <div className="flex flex-wrap items-center gap-2 border-b border-[var(--border-subtle)] p-3">
          <button className={control} type="button" aria-pressed={mode === "points"} onClick={() => changeMode("points")}>Edit Points</button>
          <button className={control} type="button" aria-pressed={mode === "links"} onClick={() => changeMode("links")}>Edit Links</button>
          {mode === "points" && <button className={control} type="button" disabled={busy || mapRevision === null} aria-pressed={adding}
            onClick={() => { setAdding(!adding); setSurface(""); setNotice(null); }}>{adding ? "Cancel adding" : "Add QR checkpoint"}</button>}
          {mode === "points" && <select className={control} aria-label="Cell tool" value={surface} disabled={busy || mapRevision === null}
            onChange={event => { setSurface(event.target.value as typeof surface); setAdding(false); }}>
            <option value="">Select checkpoint</option>
            <option value="floor">Paint floor</option>
            <option value="wall">Paint wall</option>
          </select>}
          {adding && <><input className={`${control} w-32`} type="number" min={0} max={49} value={addId}
            aria-label="New ArUco ID" placeholder="ID (optional)" onChange={event => setAddId(event.target.value)} />
            <span className="text-xs">Click an empty cell to place the checkpoint.</span></>}
          {mode === "links" && <span className="text-xs">Select two checkpoints to add or remove their corridor. {pendingLink && `Selected ${pendingLink}.`}</span>}
          <div className="ml-auto flex gap-2">
            <button className={control} type="button" aria-label="Zoom out" onClick={() => setScale(value => Math.max(.5, value - .1))}>−</button>
            <button className={control} type="button" aria-label="Reset zoom" onClick={() => setScale(1)}>{Math.round(scale * 100)}%</button>
            <button className={control} type="button" aria-label="Zoom in" onClick={() => setScale(value => Math.min(2.5, value + .1))}>+</button>
          </div>
        </div>
        {mode === "points" && checkpoint && <div className="space-y-3 border-b border-[var(--border-subtle)] p-4">
          <h2 className="text-sm font-semibold">QR{checkpoint.arucoId ?? "?"} checkpoint</h2>
          <fieldset disabled={busy} className="flex flex-wrap items-end gap-3">
            <label className={field}>ArUco ID
              <input type="number" min={0} max={49} className={`${control} w-24`} value={checkpoint.arucoId ?? ""}
                onChange={event => editCheckpoint({ arucoId: event.target.value === "" ? undefined : Number(event.target.value) })} />
            </label>
            <label className={field}>Display name
              <input className={control} maxLength={100} placeholder={mappedStation?.name || "Optional"} value={checkpoint.displayName ?? ""}
                onChange={event => editCheckpoint({ displayName: event.target.value })} />
            </label>
            <label className={field}>Operational role
              <select className={control} value={checkpointRole} onChange={event => {
                const role = event.target.value;
                editCheckpoint({ isIntersection: role === "INTERSECTION", operationalRole: role === "INTERSECTION" ? undefined : role as CheckpointRole });
              }}>
                <option value="NAVIGATION">Navigation</option>
                {Object.entries(stationRoles).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                <option value="INTERSECTION">Intersection</option>
              </select>
            </label>
            <button className={control} type="button" onClick={removeCheckpoint}>Remove checkpoint</button>
          </fieldset>
          <details className="text-xs">
            <summary className="cursor-pointer">Marker installation · {checkpoint.label}</summary>
            <fieldset disabled={busy} className="mt-2 flex flex-wrap gap-2">
              <label className={field}>Marker yaw (°)
                <input className={control} type="number" min={-180} max={180} step="any" value={checkpoint.markerYawDeg ?? ""}
                  onChange={event => editCheckpoint({ markerYawDeg: event.target.value === "" ? undefined : Number(event.target.value) })} />
              </label>
              <label className={field}>Marker size (m)
                <input className={control} type="number" min={.005} max={2} step="any" value={checkpoint.markerSizeM ?? ""}
                  onChange={event => editCheckpoint({ markerSizeM: event.target.value === "" ? undefined : Number(event.target.value) })} />
              </label>
            </fieldset>
          </details>
          {catalog && canManageStation && !catalogError && <CheckpointStationEditor key={checkpoint.label} cell={checkpoint} catalog={catalog} reload={reloadCatalog} />}
          {mapDirty && <p className="text-xs">Save map edits before changing the station association.</p>}
          {!mapDirty && !canManageStation && catalog && <p className="text-xs">Reload Backend Map to sync station associations.</p>}
        </div>}
        <div className="overflow-auto" role="region" aria-label="Scrollable warehouse map" tabIndex={0} style={{ height: 520 }}>
          <div style={{ width: `${scale * 100}%`, minWidth: 800 * scale,
            height: (simulation?.enabled && !adding && !surface ? 520 : 720) * scale }}>
            <WarehouseGrid robots={[]} animateRobots={false} grid={warehouseLayout} links={qrLinks}
              qrOnly focusQrNetwork={simulation?.enabled && !adding && !surface} catalog={catalog}
              selectedCell={mode === "points" ? selected : null} selectedPoint={pendingLink}
              onCellClick={mode === "points" ? handleCellClick : undefined}
              onPointClick={mode === "links" ? selectLinkEndpoint : undefined} />
          </div>
        </div>
        {mode === "links" && <div className="flex flex-wrap gap-2 border-t border-[var(--border-subtle)] p-3">
          {qrLinks.map(([a, b]) => <button key={`${a}::${b}`} type="button" className={control} disabled={busy}
            aria-label={`Remove link ${a} to ${b}`} onClick={() => toggleLink(a, b)}>{a} ↔ {b}</button>)}
        </div>}
      </section>
    </div>
  </main>;
}
