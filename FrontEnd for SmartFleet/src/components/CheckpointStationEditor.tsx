import { useState, type FormEvent } from "react";
import { apiClient } from "../api/client";
import { stationRoles, type Catalog } from "../api/modules/catalog";
import type { WarehouseCell } from "./WarehouseGrid";

const control = "rounded-lg border border-[var(--border-medium)] bg-[var(--surface-3)] px-3 py-2 text-sm";
const field = "grid gap-1";

export default function CheckpointStationEditor({ cell, catalog, reload }: {
  cell: WarehouseCell;
  catalog: Catalog;
  reload: () => Promise<Catalog | null>;
}) {
  const [selection, setSelection] = useState("");
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<{ message: string; error?: boolean } | null>(null);
  const stations = catalog.stations.filter(station => station.warehouseId === catalog.navigationWarehouseId);
  const mapped = stations.find(station => station.arucoId === cell.arucoId);
  const available = stations.filter(station => station.arucoId === null);
  const station = mapped ?? available.find(item => String(item.stationId) === selection);
  const creating = !mapped && selection === "new";
  const zones = catalog.zones.filter(zone => zone.warehouseId === catalog.navigationWarehouseId);
  const ready = cell.arucoId !== undefined && cell.markerYawDeg != null && cell.markerSizeM != null;
  const heading = `QR${cell.arucoId}`;
  const defaultType = cell.operationalRole && cell.operationalRole !== "NAVIGATION" ? cell.operationalRole : "PICKUP";

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const values = new FormData(event.currentTarget);
    const unlinking = values.get("unlink") === "on";
    const stationActive = values.get("active") === "on";
    if (unlinking && stationActive) {
      setNotice({ message: "Clear Active to remove the station association.", error: true });
      return;
    }
    const payload = {
      zoneId: Number(values.get("zone")), code: String(values.get("code")).trim(),
      name: String(values.get("name")).trim(), type: String(values.get("type")),
      coordX: Number(values.get("x")), coordY: Number(values.get("y")), coordZ: Number(values.get("z")),
      yawAngle: Number(values.get("yaw")), qrCodePayload: String(values.get("payload")).trim() || null,
      arucoId: unlinking ? null : cell.arucoId, isActive: stationActive,
      capacity: station?.capacity ?? 1, revision: station?.revision ?? 0,
    };
    setSaving(true);
    setNotice(null);
    try {
      const path = "/api/v1/warehouse-catalog/stations";
      if (station) await apiClient.put(`${path}/${station.stationId}`, payload);
      else await apiClient.post(path, payload);
      const refreshed = await reload();
      setSelection("");
      setNotice(refreshed ? { message: unlinking ? `Station association removed from ${heading}.` : `Station saved at ${heading}.` }
        : { message: "Station saved. Reload the catalog to see the updated association.", error: true });
    } catch (error) {
      setNotice({ message: (error as { message?: string }).message ?? "Could not save the station.", error: true });
    } finally {
      setSaving(false);
    }
  }

  if (cell.isIntersection) return <p className="text-xs">Intersection checkpoints are reserved for traffic coordination.</p>;
  if (!ready) return <p className="text-xs">Save marker installation details before associating a station.</p>;

  return <section aria-label={`${heading} station`} className="space-y-2 border-t border-[var(--border-subtle)] pt-3 text-xs">
    {mapped
      ? <p><strong>Associated station</strong> {mapped.code} · {mapped.name} · {mapped.type} · {mapped.isActive ? "Active" : "Inactive"} · {mapped.isOccupied ? "Occupied or reserved" : "Free"} / {mapped.capacity}</p>
      : <label className="flex flex-wrap items-center gap-2">Associated station
          <select className={control} value={selection} disabled={saving} onChange={event => {
            setSelection(event.target.value);
            setNotice(null);
          }}>
            <option value="">None</option>
            {available.map(item => <option value={item.stationId} key={item.stationId}>{item.code} — {item.name}</option>)}
            <option value="new">Create a station</option>
          </select>
        </label>}
    {(station || creating) && <details open={!mapped} key={station?.stationId ?? "new"}>
      <summary className="cursor-pointer font-semibold">{mapped ? "Edit associated station" : "Station details"}</summary>
      <form aria-label="Checkpoint station details" className="mt-3" key={station?.revision ?? "new"} onSubmit={event => void save(event)}>
        <fieldset disabled={saving} className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <label className={field}>Station code<input className={control} name="code" required maxLength={30} defaultValue={station?.code} /></label>
          <label className={field}>Station name<input className={control} name="name" required maxLength={100} defaultValue={station?.name ?? cell.displayName} /></label>
          <label className={field}>Station type
            <select className={control} name="type" defaultValue={station?.type ?? defaultType}>
              {Object.entries(stationRoles).map(([value, label]) => <option value={value} key={value}>{label}</option>)}
            </select>
          </label>
          <label className={field}>Zone
            <select className={control} name="zone" required defaultValue={station?.zoneId ?? (zones.length === 1 ? zones[0].zoneId : "")}>
              <option value="">Select zone</option>
              {zones.map(zone => <option key={zone.zoneId} value={zone.zoneId}>{zone.code} — {zone.name}{!zone.isActive ? " (inactive)" : ""}</option>)}
            </select>
          </label>
          <label className={field}>X (meters)<input className={control} name="x" type="number" required step="any" defaultValue={station?.coordX} /></label>
          <label className={field}>Y (meters)<input className={control} name="y" type="number" required step="any" defaultValue={station?.coordY} /></label>
          <label className={field}>Z (meters)<input className={control} name="z" type="number" required step="any" defaultValue={station?.coordZ ?? 0} /></label>
          <label className={field}>Yaw (degrees)<input className={control} name="yaw" type="number" required step="any" min={-180} max={180} defaultValue={station?.yawAngle ?? 0} /></label>
          <label className={`${field} sm:col-span-2`}>Station verification QR payload
            <input className={control} name="payload" maxLength={150} defaultValue={station?.qrCodePayload ?? ""} />
          </label>
          <div className="flex flex-wrap items-center gap-3 sm:col-span-2">
            <label><input type="checkbox" name="active" defaultChecked={station?.isActive ?? true} /> Active</label>
            {mapped && <label><input type="checkbox" name="unlink" /> Remove association</label>}
            <span>Capacity {station?.capacity ?? 1}</span>
            <button className={`${control} !bg-teal-700 !text-white`} type="submit">{saving ? "Saving…" : "Save station"}</button>
          </div>
        </fieldset>
      </form>
    </details>}
    {notice && <p role={notice.error ? "alert" : "status"}>{notice.message}</p>}
  </section>;
}
