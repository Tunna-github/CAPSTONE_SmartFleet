# SmartFleet: ArUco live warehouse map

Baseline: `Tunna-github/CAPSTONE_SmartFleet`, branch `main`, commit `b6da08a5cd38c120b1729845745292527a26771b` (2026-11-11).

This kit adds a **DISCRETE last-marker position**, not continuous metric SLAM or automatic driving. When R01 sees marker 1, the robot publishes `{"arucoId":1}` to `smartfleet/robot/R01/aruco`. The backend looks up marker 1 in R01's warehouse, saves the last fix and broadcasts `RobotPositionUpdated` via the existing `/hubs/robots` SignalR hub. The map shows R01 at the configured grid cell, e.g. A1. With no recent scan, it is labeled LAST KNOWN after 20 seconds.

## Copy/install steps (Windows)

1. Back up or commit your current repo changes (`git status` first).
2. Extract the ZIP **inside the root of CAPSTONE_SmartFleet**, next to the existing `SmartFleetBE`, `FrontEnd for SmartFleet` and `Robot code` folders. **Merge folders; do not replace existing directories.** These are new source files, plus one integration script.
3. Open a PowerShell/terminal in the repository root and run: `python apply_smartfleet_map.py` (or `py apply_smartfleet_map.py`). It patches *existing* `Program.cs`, `SmartFleetDbContext.cs`, `App.tsx`, `Sidebar.tsx`, `main_code.py`, and `qr_scanning_code.py`, saving each original as `<name>.aruco-backup`. It does not modify your existing PostgreSQL connection secrets or JWT configuration.
4. In DBeaver connect to the **same PostgreSQL database as SmartFleetBE**. Open `SmartFleetBE/Database/aruco_map_schema.sql`, execute it once and check it succeeds. It adds 2 tables and does not delete/alter existing ones.
5. Ensure `Mqtt:EnableWorkers` is `true` in your local backend configuration and the MQTT host/port point to the broker your JetRacer can reach. Do not commit passwords/keys. Start backend normally (`dotnet run` from `SmartFleetBE`). Watch for `ArUco position subscriber ready` in backend logs.
6. Start frontend from `FrontEnd for SmartFleet`: `npm install` (if needed), `npm run dev`. Sign in as Administrator and open `/admin/maps` from the existing **Warehouse Maps** sidebar entry.
7. In the admin page, select your warehouse, click cell **A1**, enter **ArUco ID = 1**, click **Save ArUco mapping**. You should see `ArUco 1 = A1` listed. The backend must have an active `Warehouse` record and an active `Robot` record with `RobotCode=R01` assigned to that warehouse.
8. Easiest smoke test: On the JetRacer Jupyter kernel, paste the entire `Robot code/aruco_live_map_jupyter.py` into one cell and run it (definitions only). In the next cell run `publish_marker(1)`. The web map should show robot R01 at **A1** with a recent timestamp. Then run `scan_camera()` and physically show the printed ArUco ID 1 to the camera. Press Interrupt to stop the scan, then run `stop_mqtt()`.
9. For your existing robot code, copy the **patched** `Robot code/main_code.py` into the Jupyter notebook you normally run for missions. Its existing movement/ACK logic stays unchanged; it now sends map marker IDs after both ArUco detection loops. Alternatively, your patched `qr_scanning_code.py` publishes map markers while its original scanner runs (and retains its existing ID-0 circle behavior, so test that with the robot safely immobilized if necessary).
10. Warehouse operators and maintenance users can view `/dashboard/map` as read-only. Administrators use `/admin/maps` to configure mappings.

## New source files to keep in their exact project paths

- `SmartFleetBE/Model/ArucoMarkerMap.cs` - persistent ArUco ID -> grid cell mapping.
- `SmartFleetBE/Model/RobotArucoPosition.cs` - most recently observed marker per robot.
- `SmartFleetBE/Controllers/WarehouseMapsController.cs` - API for listing warehouses, reading map positions, creating/removing marker mappings.
- `SmartFleetBE/Services/MQTT/MqttArucoPositionWorker.cs` - independent MQTT subscriber (not the mission ACK worker).
- `SmartFleetBE/Database/aruco_map_schema.sql` - PostgreSQL schema. Run in DBeaver, not directly from the backend.
- `FrontEnd for SmartFleet/src/api/modules/warehouseMap.ts` - typed API requests.
- `FrontEnd for SmartFleet/src/pages/WarehouseMapPage.tsx` - map configuration and SignalR display.
- `Robot code/aruco_live_map_jupyter.py` - no-motion standalone Jupyter MQTT + real camera test.
- `Robot code/aruco_live_map_jupyter.ipynb` - same test split into convenient notebook cells.

## What the script adds inside existing files

- `SmartFleetBE/Model/SmartFleetDbContext.cs`: two `DbSet<>`s and Fluent EF mappings for the new tables.
- `SmartFleetBE/Program.cs`: `builder.Services.AddHostedService<MqttArucoPositionWorker>();` beside the existing telemetry worker inside the `Mqtt:EnableWorkers` block.
- `FrontEnd for SmartFleet/src/App.tsx`: import `WarehouseMapPage`, replace the existing admin-maps placeholder, add a protected `/dashboard/map` route.
- `FrontEnd for SmartFleet/src/components/Sidebar.tsx`: add Live Map for operator and maintenance roles; the admin already has Warehouse Maps.
- `Robot code/main_code.py`: helper `report_map_marker_from_ids(ids)` after detection, called at both scan sites. **This existing script only scans while an accepted mission is running.** Use the dedicated test scanner for bench testing outside missions.
- `Robot code/qr_scanning_code.py`: adds a separate MQTT publisher to its existing scanning loop (does not alter circle control).

## API contracts

Authenticated REST endpoints (reuse existing JWT):

- `GET /api/v1/warehouse-maps/warehouses` (all 3 roles)
- `GET /api/v1/warehouse-maps/{warehouseId}` (all 3 roles)
- `PUT /api/v1/warehouse-maps/{warehouseId}/markers/{arucoId}` (Administrator only), JSON body `{"gridRow":0,"gridColumn":1}` = A1.
- `DELETE /api/v1/warehouse-maps/{warehouseId}/markers/{arucoId}` (Administrator only)

MQTT message from robot: `smartfleet/robot/R01/aruco` with payload `{"arucoId":1}`, QoS 1, `retain=false`.

SignalR event: `RobotPositionUpdated` with keys `robotId`, `robotCode`, `warehouseId`, `arucoId`, `cellCode`, `gridRow`, `gridColumn`, `seenAt`.

SignalR event after admin editing: `MapConfigurationUpdated` with `warehouseId`. The UI refreshes its snapshot.

## Configuration / assumptions

- The frontend displays an **8-row x 12-column** grid (A1..H12). To expand it, change `GRID_ROWS` and `GRID_COLS` in `WarehouseMapPage.tsx`. Backend validation supports rows 0..25 (A..Z) and columns 1..40. This first version has no draggable walls/shelves or floorplan-image overlay.
- Each marker ID is unique within one warehouse; each cell holds at most one marker mapping. ArUco ID 1 is allowed to be A1; ID 0 can be mapped too, even if the current robot notebook uses ID 0 as a movement trigger.
- The MQTT broker address on the JetRacer must match the existing robot code and the broker your backend connects to. `192.168.137.1:1883` is copied from the current robot baseline, **not a guaranteed valid address for your network**. The local `ROBOT_CODE` must match the database `Robots.RobotCode` (typically `R01`).
- The backend uses the robot's stored `WarehouseId` to resolve marker ID; the robot deliberately sends only its identity in the MQTT topic and marker ID in JSON. Do not put A1 in the robot code.
- The map reports **last observed marker**, not position between markers. Multiple visible IDs in the standalone test are resolved by choosing the visually largest marker; in the existing `main_code.py`/`qr_scanning_code.py`, the first sorted ID is used. For production use near overlapping markers, consider filtering by marker size and repeated observations.
- No existing `Robot.CurrentCoordX/CurrentCoordY` are modified: those may already represent real-world metric ROS coordinates and should not be replaced with abstract grid coordinates.
- The demo UI uses SignalR live push; fresh browser loads use REST. When SignalR disconnects, the last marker and age remain visible. It is **not a safety-grade localization or autonomous navigation system**.
- MQTT on an untrusted Wi-Fi network needs broker authentication/ACLs or transport security to prevent forged robot position messages. Use a private lab network during the test.

## Troubleshooting

- 404 or 500 from map API: Confirm backend is running the patched code and SQL ran in the correct DB. Confirm the `Warehouses` and `Robots` tables already exist.
- Page says no active warehouse: your database does not yet have an active `Warehouse` record. Create one with your existing DB/admin workflow rather than inventing warehouse IDs.
- Marker publishes but no icon: check broker host and port, `Mqtt:EnableWorkers=true`, backend `ArUco position subscriber ready`, robot code exists as active in `Robots`, that robot's warehouse matches the selected warehouse, and ArUco ID 1 is mapped in that warehouse.
- SignalR disconnected: use `VITE_API_URL`/backend CORS origins already configured for your frontend (currently localhost:5173 or :8443). JWT must be valid; sign in again if expired.
- `cv2.aruco` missing: use your JetRacer's existing `~/jetracer_aruco_py36` OpenCV contrib environment; restart the Jupyter kernel if another `cv2` was imported first.
- Live icon gray after 20 seconds: this is correct when no recent matching marker was observed. The last-known location is retained.

## Safety and verification

The code was reviewed against the current repository files and can be syntax-checked here. The lab environment does not expose your live PostgreSQL server, MQTT broker, JetRacer or .NET 10 SDK, so the integrated system still needs you to run the SQL, build and perform the Jupyter/physical tests. Keep the robot raised or safely immobilized during any test of the original `qr_scanning_code.py`, as the existing ID-0 circle trigger still operates.
