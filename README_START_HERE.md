# SmartFleet R03 physical QR0 -> QR5 pilot (Webots remains enabled)

**Source baseline:** `main` commit `8ea7468434fba66b45d1f68f4be4642054c9830c`, 27 Nov 2026.

This package is **not a completed/verified robot demo**: the Jetson, track, backend rebuild, marker detection and real motors require tests on your hardware. It is a conservative **opt-in pilot**, not a replacement for full camera+IMU localization, LiDAR obstacle avoidance, or multi-robot traffic separation. Do not use without supervision, clear floor and immediate independent physical stop.

## What this package contains

- `SmartFleet_R03_Physical_Main_V1.py`: complete R03 Jupyter/Python3 controller. Reuses your existing MJPEG webcam reader, black/white line detector, ArUco detector and Python2 ROS `/cmd_vel` watchdog. Adds strict `WAYPOINT_ROUTE`, start at QR0, pickup verification, straight line to QR5, delivery verification, mission/status/QR application ACKs, pause/cancel HOLD, bounded movement. **MOTORS_ENABLED=False** and **OPERATOR_ALIGNED=False** by default. No post-marker turn.
- `apply_r03_mixed_backend.py`: guarded backend patch for **manual** PHYSICAL R03 only, while standard Webots AUTO selects SIMULATION R01/R02. Adds one allowlisted camera-ArUco-only READY mode for the single straight QR0->QR5 pilot. The normal Webots camera+IMU validation remains unchanged. Does not fake heading or report a simulated robot.
- This README.

## 1. Git + BE (Windows laptop)

Open PowerShell at your existing SmartFleet repository root (the folder containing `compose.yaml`). **Do not run on a shared teammate's live stack with active missions.** Stop/finish demo missions first.

```powershell
cd D:\Download\GITHUB\Capstone\CAPSTONE_SmartFleet
git status --short
git switch -c feat/r03-physical-pilot
```

If you have uncommitted edits, back them up or commit before applying. Copy `apply_r03_mixed_backend.py` into your repository root, then:

```powershell
python apply_r03_mixed_backend.py --check
python apply_r03_mixed_backend.py --apply
git diff -- SmartFleetBE/Services
dotnet build .\SmartFleetBE\SmartFleetBE.csproj
```

The script backs up modified files to `.smartfleet-r03-backups/`, and won't apply when anchors don't match. If `--check` rejects, stop: the code has changed and the patch needs review. **No SQL migrations or Postgres resets are involved.** The new C# `PhysicalPilotPolicy.cs` is automatically included by SDK-style .NET projects.

### Keep existing Docker MQTT exposure and add backend pilot config

You ALREADY have a `compose.physical-mqtt.yaml`. Preserve its `mqtt` port settings and add `backend` configuration to the SAME file:

```yaml
services:
  mqtt:
    ports:
      - "192.168.137.1:1884:1883"
  backend:
    environment:
      PhysicalPilot__Enabled: 'true'
      PhysicalPilot__RobotCode: R03
      PhysicalPilot__StartArUcoId: '0'
      PhysicalPilot__DeliveryArUcoId: '5'
```

**Do not disable `Simulation__Enabled`.** Webots and BE continue to connect to `mqtt:1883`; JetRacer uses `192.168.137.1:1884`. Both addresses target the same Docker broker.

```powershell
docker compose -f compose.yaml -f compose.physical-mqtt.yaml config
docker compose -f compose.yaml -f compose.physical-mqtt.yaml up -d --build backend
docker compose -f compose.yaml -f compose.physical-mqtt.yaml ps
```

The backend may briefly disconnect Webots during rebuilding; complete all running missions first. Never use `docker compose down -v`.

To return to safe baseline, set `PhysicalPilot__Enabled: 'false'`, restart backend, and restore C# changes using Git on the feature branch. Keep your original Compose port override if you still need MQTT on 1884.

## 2. Warehouse and station configuration (website)

1. Keep the existing saved map, exact nine QR positions and all twelve QR corridors; QR0 and QR5 are **already directly linked** on `main`. Do not change **Edit Links** or Webots scene geometry.
2. In `Warehouse Maps > Edit Points`, inspect QR0 and QR5. Ensure both are **non-intersection** checkpoints. Under their station association editors: create or associate an existing **active PICKUP station at QR0**, and an **active DELIVERY station at QR5**. One station association per QR marker; don't duplicate stations. Do not move seeded P1/D1 if they are already used by Webots demo.
3. Ensure both stations have **distinct station verification payloads** (e.g. `P0` and `D5`). Those payloads are **not** ArUco navigation IDs.
4. Keep R03 PHYSICAL, active, same warehouse, not on an active task, with a **real verified payload capacity**. Its battery notebook must publish `smartfleet/robot/R03/telemetry` to the broker at 1884 continuously during the demo.
5. Create a **MANUAL** transport task selecting the QR0 pickup station and QR5 delivery station. Payload must be positive and <= the system verified limit and the real JetRacer limit. Select R03 **only after** its READY is accepted and motors have been tested.

### The intended business flow

QR0 is start AND pickup. Robot reports camera-confirmed QR0, backend records `AT_PICKUP`. Operator confirms that goods were placed on the robot using the real UI `Pickup` verification (or MANUAL with an actual reason). Only after the backend returns `CONTINUE` will R03 move. Robot follows the short actual marked lane, stops when QR5 is camera-confirmed, backend records `AT_DELIVERY`. Operator confirms unload. Backend returns `COMPLETE`, robot reports `MISSION_COMPLETED`, backend ACKs and records finished state.

**The Operator's button cannot replace a waypoint/presence report.** This pilot periodically reports actual stationary marker presence while waiting for confirmation. The backend expects fresh station presence in roughly the last 15 seconds.

## 3. Physical JetRacer (Jetson + JupyterLab)

Keep your existing ROS processes running (avoid duplicate launches):

```bash
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
roslaunch jetracer jetracer.launch
```

In separate terminals, run your existing `csi_camera.launch` and `web_video_server` commands. The pilot **uses your original HTTP MJPEG reader fed by ROS**; no new ROS camera bridge is installed. For this straight-line pilot LiDAR is not required to start, but **obstacle detection is absent**, so the route must be closed to people/objects. Keep an independent physical motor stop available.

In a DIFFERENT Jupyter notebook, keep your successful INA219 battery publisher running with:

```python
ROBOT_CODE = 'R03'
MQTT_HOST = '192.168.137.1'
MQTT_PORT = 1884
```

Stop any old `main_code.py` or another MQTT controller using `client_id='smartfleet-r03'`, otherwise the MQTT clients will evict each other.

Upload `SmartFleet_R03_Physical_Main_V1.py` to JupyterLab; open a fresh kernel or terminal and start with:

```python
%run SmartFleet_R03_Physical_Main_V1.py
```

With **both MOTORS_ENABLED=False and OPERATOR_ALIGNED=False**, it may receive MQTT messages, but it **will not claim READY, be eligible for dispatch, or command movement**. End with interrupt (Jupyter stop button). For static offline route validation, see `validate_route` in the source; it was tested with good/bad commands.

## 4. Motor test / arming sequence

1. **Do not enable motors while the robot is on a crowded floor.** First inspect the camera MJPEG stream and ArUco 4X4_50 marker IDs; verify QR0 and QR5 are actually detected. The pilot uses 5 fresh consecutive detections to confirm each marker.
2. Set `LINE_COLOR = 'BLACK'` or `'WHITE'` to match the **real tape**, and verify steering direction with the wheels lifted. This pilot retains the original P+D line follower; it **is not automatically the tuned HomeWhite V1.4.2 follower**. If the actual physical line differs, replace/retune the detector before movement.
3. Keep `MOTORS_ENABLED=False` until you've tested the control code on a bench. When supervised, with the robot aligned **towards QR5**, clear the isolated short straight route, and a person at the physical stop, set:

```python
MOTORS_ENABLED = True
OPERATOR_ALIGNED = True
```

These are deliberate local source edits followed by restart. `True` is not a general-purpose deployment setting. **The backend pilot READY is camera-ID-only**: no calibrated IMU heading is claimed. Never use this exception for curves, intersections, or multiple-waypoint routes.

4. Restart R03 controller. It requests the backend localization map/revision, confirms QR0 on **five consecutive fresh camera frames**, publishes READY and waits for backend ACK. Leave battery telemetry publishing so R03 remains ONLINE.
5. With no active task, verify R03 shows as eligible only after READY. Create one MANUAL task for the exact QR0 pickup -> QR5 delivery stations, and select R03. Follow the real two verification steps.
6. Once it reaches QR5, it stops. It **never** turns right or drives five extra seconds. After completion, it will not automatically return to QR0; reposition the robot manually with motors disabled.

## 5. Existing Webots vs physical limits

- `Simulation__Enabled` **stays true**, maintaining the current 3D world and R01/R02 AUTO behavior.
- The patch adds **only R03 to manual dispatch** when `PhysicalPilot__Enabled=true`. AUTO intentionally continues to select SIMULATION robots to prevent surprise physical motion.
- Both stacks use the same **single saved QR map and shared navigation/occupancy coordinator**. Because Webots and JetRacer are in **physically different spaces**, do not run simultaneous transport missions on overlapping QR nodes/corridors; the coordinator has no separate world/namespace yet. For truly independent simultaneous planning and traffic, add environment-aware graph/occupancy namespaces in a later BE change.
- The physical pilot accepts `ARUCO_ONLY` READY only for allowlisted stationary R03 at QR0, without claiming camera+IMU heading. Normal Webots READY remains strict. To remove this special case later, integrate calibrated camera pose + IMU on JetRacer.
- No database or volume deletion; no fake battery and no simulated package proof.

## 6. Check / rollback

After all simulations have finished, backend patch was applied and pilot enabled:

```powershell
docker compose -f compose.yaml -f compose.physical-mqtt.yaml logs --tail 120 backend
docker ps --format "table {{.Names}}\t{{.Ports}}"
```

If you see `READY REJECT`, verify R03 registration, backend rebuild, `PhysicalPilot__Enabled=true`, map revision, camera QR0 and five consecutive frames. If manual selection reports a route unavailable, check station mappings, direct QR0-QR5 link, physical/sim occupancy locks, map compatibility and fresh battery/READY. Never fake READY or clear an occupied corridor through the UI.

**Safety:** A ROS `/cmd_vel` zero command or MQTT STOP is not a certified emergency stop. Use a physical power/motor stop, low speed, an isolated lane and human supervision. The robot intentionally latches a stop on MQTT disconnect or any protocol failure; inspect the reason and restart only after making the area safe.
