# Separated lanes and post-delivery charging

This is an explicit new physical scene, `smartfleet-dual-lane-r2025a-v2`.
The old scene remains available for existing installations. Selecting the new
scene moves the original nine physical markers to a larger layout, while
retaining their IDs. Editing a logical label in the web application never moves
a physical marker or changes a Webots world.

## Business flow

1. Operator creates a transport task using the original transfer points QR0,
   QR2–QR8. QR1 remains reserved, unavailable as a goods-transfer stop.
2. Camera + IMU initialize position and heading automatically. Every road
   segment requires an ordered backend navigation grant.
3. Operator confirms pickup and delivery beside the live tracking view. The
   completed transport mission and task stay COMPLETED.
4. Completion atomically creates a persisted `ChargingJob`. Robot remains BUSY,
   so manual and AUTO assignment cannot steal it between task completion and
   return-to-charge creation.
5. The backend reserves one available CHARGING station before sending its
   frozen return route. Four independent pull-through bays share a charging
   zone, with separate entry and exit buses. If no safe route/bay is available,
   the robot waits in its transfer bay outside the through lanes.
6. Ordered, camera-confirmed return progress is committed before each ACK.
   At its reserved final QR the robot reports stopped. Only with fresh device
   presence, fresh stopped camera reports, no safety fault/maintenance, and
   battery **at least 20%** does it become READY / AVAILABLE.
7. Manual or AUTO dispatch marks the old charging cycle DEPARTING. The berth
   remains reserved until the next transport route confirms a checkpoint
   outside it. This prevents another returning robot using the same bay while
   the first robot is still leaving.

The battery increase while docked is a Webots-only simulation model. A physical
robot requires its real dock/BMS safety handshake; simulated ground-truth poses
never authorize arrival, handover, charging readiness or resource release.

The charging profile uses 1.25× straight cruise speed (2.75 rather than 2.2
wheel rad/s) to shorten simulation testing. Near-QR, final approach, turning and
overshoot speeds remain unchanged. `SIMULATION_CRUISE_SCALE` can override the
cruise multiplier within 0.5–1.35. Network presence uses a wall-clock heartbeat,
so slow rendering does not stretch a simulation-time telemetry interval.

## Physical layout and QR responsibilities

- Existing service IDs: 0–8; QR1 remains reserved.
- Each grid location has a directed circulation ring and an internal stop bay.
  Opposite through directions use different physical lanes and checkpoints.
  Shared bay spurs use current/next occupancy and exit-clear checks.
- Lane spacing on ordinary opposing corridors is 1 metre. The commissioning
  grid scale remains 0.5 metre/cell, 18 rows × 28 columns.
- Charging bays CHG1–CHG4 use QR67–QR70, capacity one each, between the incoming
  bus (row 15) and outgoing bus (row 17).
- There are 71 physical markers and 109 ordered edges. The expanded layout uses
  OpenCV `DICT_4X4_250`; IDs 0–49 have the same bit patterns as the earlier
  `DICT_4X4_50`. Auxiliary lane/charging markers are not selectable transport
  pickup/delivery points.
- The simulator observer supplies visualization only. Actual movement still
  uses camera ArUco, IMU, wheel-encoder travel bounds, and Webots laser readings.

The backend rejects travel against ordered links. It checks a planned bay is
clear before entering its shared merge, so arrivals do not block departures.
Through lanes use current/next reservations; blindly requiring two empty lane
checkpoints can itself deadlock two robots on a small circulation ring.
Occupancy survives disconnects and backend restarts; a timeout cannot clear it.
Routes initially avoid occupied checkpoints. Temporary traffic can still cause
safe waiting; a physically blocked route is not declared clear automatically.

## Lost QR, faults and recovery

After a previously seen QR disappears, the robot stops and retries stationary
camera acquisition for up to eight seconds. Encoders bound travel on a segment
whose expected QR has not appeared. Neither encoders nor simulation poses count
as a reached QR. An unresolved loss becomes a fault and retains resources.

All roles can Pause a charging/returning robot from Live tracking or the global
Robot safety panel. Maintenance alone can resume after a controller stop ACK,
physical inspection, fresh localization and resolution of serious incidents.
Resumption increments a durable generation so delayed commands cannot restart
an earlier movement. A controller restart during return requires recovery.

During a transport departure, use the existing task Pause/cancellation controls.
A valid cancellation HOME acknowledgement releases a vacated charging berth.
If a departure was interrupted before any confirmed exit, Maintenance must
complete task/load recovery and obtain fresh camera confirmation outside the
bay before using **Release vacated charging bay**. This does not make a robot
AVAILABLE or clear its Maintenance/task safety holds.

## Map creation and editing

Admin → Warehouse Maps supports both legacy undirected links and ordered lanes.
An ordered link is source → destination; deleting it preserves a separately
configured reverse bay spur. Links must be straight, cannot skip a checkpoint,
and cannot cross without a shared checkpoint/traffic coordination. Opposing
through edges on the same pair of markers are rejected.

Directionality and ArUco dictionary survive a save/reload, alongside marker
orientation, physical size and operational role. Readiness checks include return
connectivity for a directed graph. Lane markers are visually smaller on the 2D
map; transfer points, charging bays and robot positions remain distinct.

**Load commissioned scene template** loads the active server scene as a draft;
only **Save to Database** persists it. Logical display names are retained by
marker ID. Scene validation includes marker family, positions, orientations,
sizes, reserved intersections, physical bay roles and ordered corridors. A
geometry mismatch blocks simulation dispatch. Map/station edits are blocked
while assignments, failed load holds or unfinished charging cycles exist.

The reproducible builder is `tools/build_dual_lane_scene.py`. Its checked-in
outputs are the Webots world, QR textures, map JSON and manifest. Other machines
pull these artifacts and build Docker images; they do not have to regenerate
assets or install Webots locally. A custom physical layout requires explicit
changes to the world and its commissioned manifest, not merely web labels.

## Enable on an existing local demo

Use PowerShell in the repository. Finish/recover all ACTIVE assignments and
reserved berths first. Take a PostgreSQL backup before changing physical layout.
The upgrade rejects active work, failed holds, occupied/reserved stations and
customized physical geometry. It archives the old map and preserves historical
tasks, missions, verification records, incident history and robot status.

```powershell
docker compose stop backend webots
python tools/setup_demo.py --charging-layout
docker compose up -d --build
```

Open `http://localhost:8080/simulation` and refresh the browser. Use the same
accounts as the existing demo. The new profile has separate simulator/backend
state volumes, avoiding reuse of old-scene navigation state. Database data is
retained and commissioning is recorded as demo bootstrap version 6.

The setup command stores the platform-correct `COMPOSE_FILE` setting in the
private `.env`, so normal `docker compose` commands keep using both files.
Starting only the legacy profile against the new map intentionally
fails scene compatibility. Rebuilding/restarting a simulator while robots are
working is an interruption requiring recovery; it does not reset task locks.

## Independent installation

On a machine with Docker Desktop running Linux containers:

```powershell
git clone --branch feat/task-cancellation https://github.com/Tunna-github/CAPSTONE_SmartFleet.git
cd CAPSTONE_SmartFleet
python tools/setup_demo.py --charging-layout
docker compose up -d --build
```

The setup script creates private credentials in `.env`; use those passwords.
Generated credentials and database/state volumes do not belong in Git.

For isolated acceptance testing on the development machine:

```powershell
$env:WEB_PORT='8089'
docker compose -p smartfleet-charging-final -f compose.yaml -f compose.charging.yaml up -d --build
python tools/check_charging_simulation.py
```

The test creates real Webots missions, confirms simulated handovers, checks
return/docking, simultaneous distinct bay reservations, blocked early dispatch,
Maintenance-only recovery and AUTO redispatch. It also records minimum measured
robot separation. It is restricted to port 8089 and requires a clean isolated
test installation. Do not run it against operational missions.

Automated checks also include `ChargingTests`, `test_charging_protocol.py`,
`charging-map.spec.ts`, `check_lane_upgrade.py`, and the existing browser
simulation check. PostgreSQL commissioning checks use disposable clones, with
the main database read only.

See [validation results and test limits](dual-lane-validation.md) for the
recorded two-robot run, recovery steps and local installation checks.
