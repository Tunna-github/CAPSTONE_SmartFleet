# SmartFleet: Weighted A* + Multiple Webots Robots (R01, R02)

**Purpose:** Extend the working single-robot CAPSTONE pilot to (1) cost-aware Backend route selection and (2) independent R01/R02 Webots simulations. **No SQL migrations, no FE replacement, no Git push.**

**Important scope:** This installer **does not** implement multi-robot collision avoidance. The Backend already has intersection reservation logic, but the Webots controller does **not** yet enforce `NAV_CONTROL` STOP/GO grants or publish the complete APPROACH/ENTER/EXIT cycle. Use separate, nonintersecting lanes until that integration is finished. Do not test competing paths through the same QR intersection yet.

## What the installer changes

- `SmartFleetBE/Services/QrDemoRouteService.cs`: replaces distance-only A* with **weighted A***. It keeps the same Backend map/edges, the same MQTT route payload and the same original QR position/progress tracking. Cost = grid-distance travel-time proxy + 90°/180° turn penalty + active Backend intersection queue/occupancy penalty + **other active missions' upcoming intersection demand**. Safety-blocked intersections cannot be entered by newly planned routes. It carries the arrival heading from the pickup leg into the delivery leg. This optimization is decided *once at assignment*, not continuously during movement.
- `Webots_Full_Mission_Controller/my_controller_testing_fleet.py`: changes `ROBOT_CODE` and first-start calibration QR/heading to read `controllerArgs`. All existing camera recognition, arrival control, steering, IMU, mission ACKs, safety commands (if present), and MQTT topic construction remain as they were.
- Optionally patches the actual Webots world controller supplied with `--webots` (recommended), while preserving that actual controller's arrival fixes.

### Cost settings (optional)

These are **model tuning proxies**, not calibrated real-world seconds or guarantees. The patch uses these defaults even if you do not edit `appsettings.json`. To override, add the nested `Planner` object **inside your existing `DemoRoute` section**, keeping its other keys intact:

```json
"Planner": {
  "SecondsPerGridCell": 1.0,
  "TurnPenaltySeconds": 1.5,
  "UTurnPenaltySeconds": 3.0,
  "WaitRobotPenaltySeconds": 4.0,
  "OccupiedIntersectionPenaltySeconds": 6.0,
  "PlannedMissionPenaltySeconds": 2.0
}
```

**Not implemented yet:** obstacle-aware edge costs (there is no per-road obstruction feed), calibrated ETA, mid-mission rerouting, actual intersection stop/go enforcement, guarantees against head-on collision.

## 1. Apply the local patch

**First:** stop the Backend when no mission is active and pause Webots. Keep **all physical JetRacer scripts using `R01` or `R02`** stopped, or they can conflict with simulated robot identities again.

Copy `SmartFleet_Weighted_AStar_MultiWebots_Setup.py` to your repository root. In Windows CMD:

```bat
cd /d "D:\Donwload\GITHUB\Capstone\CAPSTONE_SmartFleet"
py -3 ".\SmartFleet_Weighted_AStar_MultiWebots_Setup.py" --repo . --webots "C:\Users\DELL\OneDrive\Documents\turtlebot\controllers\my_controller_testing_fleet\my_controller_testing_fleet.py" --dry-run
```

Confirm it lists the Backend route service, the repo controller, and the actual Webots controller. Then **run the exact same command without `--dry-run`**. The installer saves backups to the Windows temporary directory, *outside the repository*, and refuses to run if it cannot find the expected source-code anchors. It is safe to rerun (idempotent).

Build and restart the Backend:

```bat
dotnet build ".\SmartFleetBE\SmartFleetBE.csproj"
cd SmartFleetBE
dotnet run -lp https
```

Do not proceed if the build fails. The authoring environment did not have .NET installed, so full compilation has not been performed here.

## 2. Create R02 in the same Webots world

1. Save a backup of your current `.wbt` world.
2. In Webots, **pause** simulation. Select your working `TurtleBot3Burger` *Robot node*, not its camera or a random Solid.
3. Duplicate that Robot node using **Copy / Paste** in the Scene Tree (or duplicate through the Webots UI). This creates a second robot with identical camera, motors, IMU, and controller.
4. Expand both robot nodes and set their `name`, `controller` and `controllerArgs` fields:

| Robot | Webots `name` | `controller` | `controllerArgs` |
|---|---|---|---|
| First | `SmartFleet_R01` | `my_controller_testing_fleet` | `--robot-code=R01 --start-qr=0 --start-heading=S` |
| Second | `SmartFleet_R02` | `my_controller_testing_fleet` | `--robot-code=R02 --start-qr=5 --start-heading=S` |

**Crucial:** `controllerArgs` is a space-separated string. If your Webots UI displays a string list instead of a text line, use the editor control to supply the arguments in the supported field format. Both robots share the **same controller file** but Webots launches an independent process for each.

5. Set robot R01's `translation` to the **actual QR0 marker** and robot R02's `translation` to **the actual QR5 marker**, leaving enough distance between robots. Do **not** guess world X/Y/Z from the Backend's grid `row`/`col`—they are different coordinate systems.
6. Keep R02's `rotation` the **same as R01** for the example above, so `--start-heading=S` is appropriate. If you rotate R02 differently, set `--start-heading=N/E/S/W` to match its **actual** orientation. This calibration choice is essential for correct IMU turns. Check that the camera preview shows the expected ArUco before dispatch.
7. Save `.wbt`, reset Webots, and run. If your copied PROTO cannot expose `controllerArgs`, set it through the Webots Robot fields or edit the Robot node in the `.wbt` world using Webots; do not duplicate the entire controller folder and hardcode R01/R02.

The controller has a backwards-compatible R01 fallback for single-robot tests, but **both robots must have explicit different `--robot-code` arguments** when testing two robots. Otherwise they both publish as R01.

## 3. Check Backend readiness before assigning anything

Expect **two separate** lines (possibly interleaved):

```text
[BOOT] SmartFleet Webots CAMERA ArUco full-mission driver R01
[BOOT] SmartFleet Webots CAMERA ArUco full-mission driver R02
[CAMERA] Localized at QR0 ...
[CAMERA] Localized at QR5 ...
[READY ACK] Backend recorded current QR 0
[READY ACK] Backend recorded current QR 5
```

Use Swagger with a valid operator/admin JWT:

```http
GET /api/v1/demo-routing/robots/positions
```

Expected shape:

```json
{"R01": 0, "R02": 5}
```

If R02 does not appear, check its console, `controllerArgs`, Broker IP/port, calibration and that the **existing** SmartFleet R02 record is `ONLINE`, `AVAILABLE`, in the correct warehouse, with sufficient battery and payload capacity. Do not run an old physical R02 controller at the same time. R02 already appears in your Backend test records; do not add/modify DB records just to duplicate the Webots model.

## 4. Test routes without collision risk

Start with the following **nonoverlapping column routes**:

| Webots robot | Start QR | Pickup QR | Delivery QR | Example route |
|---|---:|---:|---:|---|
| R01 | QR0 | QR1 | QR2 | `QR0 -> QR1 -> QR2` |
| R02 | QR5 | QR4 | QR3 | `QR5 -> QR4 -> QR3` |

For each: SmartFleet FE -> Create Transport Task -> MANUAL, select **separate pickup/drop ArUco IDs**, use valid warehouse/station IDs, and assign the correct robot. Check that the assigned robot's own Webots console prints the mission and camera checkpoints. First run one mission at a time; then try overlapping mission times **only on physically disjoint corridors**.

The two routes above are intentionally separate. Do not send both robots through QR1 or the same road until NAV_CONTROL integration is complete and tested.

## 5. How to verify weighted A*

Every new mission log includes the computed route, e.g.:

```text
Demo QR mission 65: start QR0, pickup QR2, drop QR6, path ...
```

The cost model can prefer fewer turns or avoid an already busy/locked intersection when the graph offers a valid alternative. It uses:

- **Distance/time proxy** = Euclidean length between axis-aligned QR nodes × configured seconds per grid cell.
- **Turn penalty** = 0 straight, configured value for 90°, different value for 180°.
- **Congestion estimate** = Backend's current `WaitingCount` / `InsideCount` at configured intersections and other missions' upcoming 4 QR waypoints.
- **Blocked intersection** = `BlockedPendingPhysicalClearance` prevents a new route through that QR.

Congestion snapshots are not safety permissions. Both routes use the same Backend map. Changes to map links only affect **future** missions. The current MQTT `WAYPOINT_ROUTE` command is unchanged.

## 6. Before testing shared intersections (next milestone)

The existing Backend uses `smartfleet/robot/<Rxx>/scan` and `/navigation`, and publishes `/nav-control` messages. Currently the Webots controller does not honor those NAV_CONTROL messages.

Before two robots can safely compete at QR1:

1. Mark QR1 as an intersection in the FE and save the map.
2. Before entering, each robot must publish the planned **current QR and next intersection QR**, hold position at an appropriate stop line and await a **fresh GO/grant**.
3. The Webots controller must subscribe to `/nav-control`, verify `robotCode`, `epoch`, `sequence`, `expiresAtUtc`, and **STOP on missing/stale/STOP grants**. The current Backend requires recent safe LiDAR information (`CLEAR`/`DETECTED` within 2.5 seconds); a fake CLEAR value must never be used as physical collision protection.
4. A granted robot must report `ENTER` and, after physically clearing the intersection, `EXIT`. Do **not** release the lock merely because a mission completed or a timeout elapsed.
5. Test grant loss, Backend restart, obstacle, two same-time arrivals, and cancellation. Require a human-clear recovery for an unresolved physical blockage.

**Until step 2–5 are implemented, the routing cost model alone cannot prevent collisions.**

## QR ID mismatch to resolve before testing every route

Your current map snapshot has `label: "QR08"` but `arucoId: 9` at the upper-right node. The label is decorative: if the **actual** Webots marker ID is 8, correct that configuration before using a route through the upper-right node. Weighted A* can choose alternative paths through this node, so a mismatched ID can look like a navigation failure.
