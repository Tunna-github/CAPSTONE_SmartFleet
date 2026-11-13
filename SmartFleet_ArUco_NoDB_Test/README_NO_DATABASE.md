# SmartFleet ArUco live map - NO DATABASE test mode

Based on your already-patched SmartFleet project (after `python apply_smartfleet_map.py` showed six [OK] lines).

## What it does

- Reuses your existing React Live Warehouse Map page unchanged.
- Reuses your existing `Program.cs` registration for `MqttArucoPositionWorker` unchanged.
- Replaces the **database-backed** map controller and MQTT worker with **memory-backed** versions.
- Adds `MapDemoStore.cs`, a thread-safe shared in-memory map.
- The in-memory map provides a fake warehouse **DEMO / ID 1** and robot **R01 / ID 1** so **no PostgreSQL table/record is needed for this feature**.
- Configure `ArUco 1 -> A1` in the web; send `{"arucoId":1}` to MQTT topic `smartfleet/robot/R01/aruco`; the map displays R01 at A1 immediately via SignalR.
- Mapping and last scan **reset whenever backend restarts**. This is intentional for testing.

## Install on Windows WITHOUT changing database

1. Ensure the previous `apply_smartfleet_map.py` already succeeded. **Do not run it again.**
2. Extract this ZIP into `D:\Download\GITHUB\Capstone\CAPSTONE_SmartFleet\SmartFleet_ArUco_NoDB_Test` (a subfolder is fine).
3. Open CMD in the `CAPSTONE_SmartFleet` repository root.
4. Back up your original DB-backed map files (optional but strongly recommended):

```cmd
copy "SmartFleetBE\Controllers\WarehouseMapsController.cs" "SmartFleetBE\Controllers\WarehouseMapsController.cs.db-version-backup"
copy "SmartFleetBE\Services\MQTT\MqttArucoPositionWorker.cs" "SmartFleetBE\Services\MQTT\MqttArucoPositionWorker.cs.db-version-backup"
```

5. Copy only the three new/replacement C# files into the existing backend:

```cmd
robocopy "SmartFleet_ArUco_NoDB_Test\SmartFleetBE" "SmartFleetBE" /E
```

`robocopy` exit codes 0-7 mean normal/successful operation; codes 8+ mean an error.

6. **Do not run any SQL, migrations, `dotnet ef database update`, or `aruco_map_schema.sql`.**
7. In the backend folder, build/run:

```cmd
cd SmartFleetBE
dotnet build
dotnet run
```

8. In a second terminal, start frontend (if not running):

```cmd
cd "D:\Download\GITHUB\Capstone\CAPSTONE_SmartFleet\FrontEnd for SmartFleet"
npm run dev
```

9. Log in with your normal SmartFleet admin account (the **normal app login may read your existing DB**, but **this map feature makes zero database calls**). Open `/admin/maps`.
10. Select A1 -> type ArUco ID `1` -> Save. The dropdown will say `DEMO - Map Test (No Database)`.
11. On your JetRacer in **standalone no-motion Jupyter notebook** `Robot code/aruco_live_map_jupyter.ipynb`, run `publish_marker(1)` and then `scan_camera()` to test real scanning.

Expected backend logs:

```text
ArUco NO-DB MAP DEMO subscriber ready (R01).
DEMO MAP: R01 saw ArUco 1 at A1
```

Expected UI: R01 icon in cell A1, timestamp 'RECENT FIX', SignalR CONNECTED.

## MQTT / SignalR wiring

- MQTT broker host/port are reused from existing `Mqtt` configuration and the standalone notebook.
- `Mqtt:EnableWorkers` must already be `true` because the earlier integration registered the map worker inside that block. Your original backend workers will also run under that flag.
- MQTT topic: `smartfleet/robot/R01/aruco`
- Payload: `{"arucoId":1}` (do not send `cellCode` from robot)
- SignalR route: `/hubs/robots` with event `RobotPositionUpdated`.
- Only marker IDs configured in the web are accepted. Initially the mapping list is empty.
- This simple test supports one simulated identity `R01` regardless of existing `Robots` database rows. It does **not** overwrite robot telemetry/coordinates or control its motors.

## Restore the DB-backed version when permissions are available

Stop backend. Restore the two backup files (using CMD in root):

```cmd
copy /Y "SmartFleetBE\Controllers\WarehouseMapsController.cs.db-version-backup" "SmartFleetBE\Controllers\WarehouseMapsController.cs"
copy /Y "SmartFleetBE\Services\MQTT\MqttArucoPositionWorker.cs.db-version-backup" "SmartFleetBE\Services\MQTT\MqttArucoPositionWorker.cs"
```

You can leave `MapDemoStore.cs` unused or remove it. Then, separately, with DB permissions, apply your original SQL to support persistent map storage. **Never run that SQL without authorization.**

## Common issues

- **Map API returns 404/500:** ensure you copied files correctly and restarted/backend recompiled, then sign in again.
- **Map shows no robot update:** check in frontend SignalR CONNECTED, in backend worker-ready log, both PC and robot connect to the same MQTT broker, ROBOT_CODE is R01, and ID 1 is mapped in the web.
- **No worker-ready log:** `Mqtt:EnableWorkers` is false, broker is unreachable, or the BE cannot start. Inspect logs. Do not enable other robot workers without understanding which ones are already active.
- **Login fails:** unrelated to this map. The existing main app still uses database-backed authentication. If you require *fully offline login-free* testing, a separate standalone demo server would be needed.
- **Scanner drives unexpectedly:** use the standalone `aruco_live_map_jupyter.ipynb` instead of `qr_scanning_code.py`, because the latter retains its existing ID 0 movement trigger.
- **No DB schema:** correct. This mode never queries ArucoMarkerMaps or RobotArucoPositions.
