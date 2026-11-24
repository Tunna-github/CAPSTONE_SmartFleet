# Automatic ArUco + IMU startup localization

The robot discovers a visible QR and its orientation on every startup. Neither
`--start-qr` nor `--start-heading` is used. Old arguments are ignored with a log
message, and the previous `smartfleet_heading_calibration_Rxx.json` file is not
used as evidence of the current orientation.

## Installation calibration

Each QR cell in the PostgreSQL navigation map has `markerYawDeg` and `markerSizeM`.
Yaw describes the physical marker's canonical +X axis in `MAP_N_W_UP`: 0° points
North, +90° West, -90° East. Size is the measured full marker width including the
black border. Configure these when installing or moving a marker, not on each boot.
The map editor preserves these values and exposes them when editing a QR cell.

`aruco_camera_calibration.json` describes camera resolution, optics and the fixed
camera-to-robot rotation. The included file was derived from the actual R2025a
TurtleBot3Burger camera mounting in this project's world. Its ideal pinhole optics
are specific to this simulation. A physical camera needs measured intrinsics,
distortion coefficients and mounting calibration. A resolution/FOV mismatch holds
the robot instead of silently reusing the wrong calibration.

To prepare marker installation metadata from the existing world and a map export:

```powershell
python tools/commission_webots_markers.py --world D:/turtlebot/turtlebot/worlds/turtlebot3_burger.wbt --map map-export.json --output commissioned-map.json
```

The tool decodes the real marker textures, checks physical positions against the
map grid, rejects mirrored/inconsistent layouts, and exports calibration. It does
not update the database or move any robot. The installed nine-marker map was
commissioned and saved as revision 5; geometry and links were preserved.

## Runtime protocol

1. Robot sends `LOCALIZATION_CONFIG` on its existing `/qr-progress` MQTT topic.
2. Backend replies on `/localization-map` with the matching event ID and the
   current database map revision and marker calibration.
3. Camera detects ordered ArUco corners. `solvePnPGeneric(IPPE_SQUARE)` estimates
   marker-to-camera rotation. The marker/map and camera/robot transformations give
   continuous robot yaw in the map frame.
4. Projection error, marker image size, gravity direction and pose ambiguity are
   checked. Five stable observations align the current IMU session to the map.
5. READY includes `headingDeg`, `localizationSource=ARUCO_IMU`, `mapRevision`,
   `reprojectionErrorPx`, and `stableFrames`. Backend rejects missing, stale-map,
   uncommissioned or low-quality pose reports. Dispatch requires fresh localization.
6. IMU tracks heading during travel. At camera-confirmed checkpoints it is checked
   against marker pose; the planned route direction is never used as calibration.

The first turn uses the measured continuous angle, including non-cardinal starts.
Idle camera loss, a changed marker/orientation, or reconnect revokes READY and
requires localization again. Map updates invalidate the previous revision's pose.
The robot keeps motors stopped while startup localization is uncertain.

Keep the robot ID and any required traffic settings in controllerArgs, e.g.:

```text
--robot-code=R01 --intersection-pilot=true --sim-lidar=true --traffic-test-hold=8
```

`--sim-lidar=true` is only for Webots. No robot queries PostgreSQL directly; the
backend supplies marker installation data and computes mission waypoints.

## Validation

```powershell
python -m unittest discover -s tests -p "test_*.py" -v
dotnet test tests/SmartFleetBE.Tests/SmartFleetBE.Tests.csproj
cd "FrontEnd for SmartFleet"
npm run build
npm run test:web
```

An isolated Webots world, separate MQTT broker on 1884, and test-only Supervisor
were also used. Five robots started at different markers facing 0°, 180°, +90°,
-90° and 45°. All localized with less than 0.5° heading error, despite deliberately
wrong legacy start arguments. A subsequent 180° manual rotation was detected and
relocalized. The Supervisor provided test assertions only; runtime localization
never reads robot/world ground truth. No production tasks were dispatched by that test.

The final controller was also tested on the generated route QR0 → QR1 → QR7 in a
separate single-robot Webots world. Initial measured heading was about +89.5° (W),
while the deliberately wrong deprecated argument said E. Camera-confirmed
waypoints were acknowledged in order, including the turn at QR1; the mission
completed and localized again at QR7 at about -90.4°. The test took 31.6 simulated
seconds. The payload came from the actual backend route service; MQTT ACKs in
this isolated motion test were supplied by a test responder, without inserting
production assignments. Traffic arbitration was not part of this motion test.

The canonical repository controller and the installed controller were synchronized
to preserve Pause/Cancel safety handlers together with the existing camera
approach tuning. R01 and R02 were then restarted against the actual backend and
broker on 1883, without either start argument. Both received accepted ARUCO_IMU
READY acknowledgements on database map revision 5.
