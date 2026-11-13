# Manual code insertions, if you do not want to run apply_smartfleet_map.py

These are the only existing files that need editing. The complete implementations of all NEW files are already included under the correct project folders in this kit. Use this manual guide only if you are not using the auto-apply script.

## SmartFleetBE/Model/SmartFleetDbContext.cs

Immediately after the existing `MapConfigurations` DbSet add:

```csharp
public virtual DbSet<ArucoMarkerMap> ArucoMarkerMaps { get; set; }
public virtual DbSet<RobotArucoPosition> RobotArucoPositions { get; set; }
```

At the START of `OnModelCreating(ModelBuilder modelBuilder)` add, BEFORE the existing `RobotCommandOutbox` mapping:

```csharp
modelBuilder.Entity<ArucoMarkerMap>(entity =>
{
    entity.ToTable("ArucoMarkerMaps");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).ValueGeneratedOnAdd();
    entity.Property(e => e.CellCode).HasMaxLength(16);
    entity.HasIndex(e => new { e.WarehouseId, e.ArucoId }).IsUnique();
    entity.HasIndex(e => new { e.WarehouseId, e.GridColumn, e.GridRow }).IsUnique();
    entity.HasOne<Warehouse>().WithMany().HasForeignKey(e => e.WarehouseId)
        .OnDelete(DeleteBehavior.Cascade);
});

modelBuilder.Entity<RobotArucoPosition>(entity =>
{
    entity.ToTable("RobotArucoPositions");
    entity.HasKey(e => e.RobotId);
    entity.HasOne<Robot>().WithMany().HasForeignKey(e => e.RobotId)
        .OnDelete(DeleteBehavior.Cascade);
});
```

## SmartFleetBE/Program.cs

In the `if (enableMqttWorkers)` block, immediately AFTER `builder.Services.AddHostedService<MqttRobotTelemetryWorker>();`, insert:

```csharp
builder.Services.AddHostedService<MqttArucoPositionWorker>();
```

The existing `using SmartFleetBE.Services;` already covers the new worker. Keep the existing mission status worker unchanged.

## FrontEnd for SmartFleet/src/App.tsx

Near the imports, add:

```tsx
import { WarehouseMapPage } from "./pages/WarehouseMapPage";
```

Inside the shared permissions block for `operator`, `admin` and `maintenance`, beside the existing `/dashboard/robots` route, add:

```tsx
<Route path="/dashboard/map" element={<WarehouseMapPage />} />
```

Find the existing admin route `path="/admin/maps"` with `Admin Maps (Coming Soon)` and replace ONLY its route element with:

```tsx
<Route path="/admin/maps" element={<WarehouseMapPage />} />
```

## FrontEnd for SmartFleet/src/components/Sidebar.tsx

The administrator `Warehouse Maps` link already exists. Add the following object to BOTH the `maintenance` and operator nav item arrays, beside their `Fleet Status` entry:

```tsx
{ id: "map", label: "Live Map", path: "/dashboard/map", icon: IC.maps },
```

## Robot code/main_code.py

Near `def wait_for_aruco_id(target_id, timeout=None):` (BEFORE that function) add:

```python
_last_sent_map_marker = None
_last_sent_map_at = 0.0

def report_map_marker_from_ids(ids):
    global _last_sent_map_marker, _last_sent_map_at
    if not ids or not mqtt_connected.is_set():
        return
    marker_id = int(ids[0])
    now = time.monotonic()
    if marker_id == _last_sent_map_marker and now - _last_sent_map_at < 5.0:
        return
    result = client.publish(
        "smartfleet/robot/{}/aruco".format(ROBOT_CODE),
        json.dumps({"arucoId": marker_id}),
        qos=1, retain=False)
    if result.rc == mqtt.MQTT_ERR_SUCCESS:
        _last_sent_map_marker = marker_id
        _last_sent_map_at = now
        print("[MAP] sent ArUco {}".format(marker_id), flush=True)
```

In BOTH `wait_for_aruco_id(...)` and `follow_black_line_until_aruco(...)`, find:

```python
ids = detect_aruco_ids_from_frame(frame)
```

Immediately below (same indentation for each location), add:

```python
report_map_marker_from_ids(ids)
```

This publishes to MQTT only during missions; it does not alter the existing motor commands or mission ACK logic.

## Robot code/qr_scanning_code.py (optional, if you use this notebook as the active scanner)

This scanner has no original MQTT connection. The auto-apply script adds a separate MQTT client to it, reads the already-validated `detected_ids = current_ids` after the stale-frame check, sends `{"arucoId": marker_id}` once per marker change or every 5 seconds, and closes that publisher in its existing `finally`. For full code, use the `patch_qr_scanner()` section in `apply_smartfleet_map.py`. You can instead use the standalone `aruco_live_map_jupyter.ipynb` for a motion-free Jupyter test without editing the existing QR scanner.

## Run/test

1. Run `SmartFleetBE/Database/aruco_map_schema.sql` in DBeaver (database used by BE).
2. Start backend with MQTT workers enabled, then start frontend.
3. Admin: `/admin/maps` -> click A1 -> ArUco ID 1 -> Save.
4. Robot Jupyter: run the provided `aruco_live_map_jupyter.ipynb` -> `publish_marker(1)` -> verify R01 at A1 -> `scan_camera()` for actual marker.
5. If desired, replace robot's current Jupyter mission script with the patched `main_code.py` and test marker events during an actual mission.
