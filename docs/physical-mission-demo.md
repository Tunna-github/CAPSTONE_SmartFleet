# Physical demo: follow line + ArUco checkpoint events

For the deployed R03/Map 1 setup and copyable notebook cell, see the Vietnamese
[home test guide](physical-demo-home-test.md). It records startup, recovery,
current state and the distinction between software and physical validation.

This is the current requested physical demo mode: `MARKER_LINE_DEMO`.
The calibrated metric-pose adapter remains optional; its measurements do NOT
block this mode.

## Business flow

1. Start the controller once. It connects to MQTT and reports its visible start
   checkpoint while stopped. No start-heading/start-qr arguments are needed.
2. Operator creates/assigns a normal SmartFleet task using its pickup/delivery
   stations. Backend StationId mapping supplies the frozen waypoint route.
3. If necessary follow the black line toward pickup. At pickup, stop and wait
   for the Operator's existing station/package verification.
4. After backend CONTINUE, follow the line to the next expected ArUco checkpoint.
   A target detection stops motion immediately; two distinct camera frames and
   actual stopped feedback confirm it. Other visible IDs cannot advance progress.
5. At delivery stop and wait for delivery verification. Only backend COMPLETE
   permits MISSION_COMPLETED.
6. Return to IDLE, keep MQTT/camera/telemetry running, report the current QR and
   wait for the next task. A completed task does not stop the notebook/process.

Navigation is pixel-based line following. No camera matrix, lens mounting pose,
marker dimensions, encoder metre calibration, stopping distance, arrival radius
or global heading is required for this demo. Encoder/IMU feedback is still used
for stopped/moved checks and telemetry. Session odometry is the driver's reported
metres (an estimate); it is not used to decide checkpoint arrival.

## Files and startup

Use `Robot code/physical/SmartFleet_Physical_Demo.ipynb` with the adapter files in
one new Jetson folder `~/Public/SmartFleet_Physical_Demo`. Copy
`line-demo-profile.example.json` to `physical-profile.json`. Match robotCode,
mqttClientId and brokerHost to the registered physical device. Existing Python
3.6/OpenCV 4.6/paho and Python2 ROS Melodic are reused; nothing is installed.
Existing driving notebooks, including Keyboard Lab, must release cmd_vel first.
Keep the existing ROS robot/camera/LiDAR terminals running; the adapter does not
launch them again. LiDAR front orientation can come from the existing ROS TF.

The backend opt-in for this device is:

```text
PhysicalRobots:Enabled = true
DemoRoute:Enabled = true
PhysicalRobots:LineDemoRobotCodes:0 = R03
Charging:Enabled = false
```

Only the explicitly listed registered PHYSICAL identities may send
ARUCO_LINE_DEMO reports. Strict camera/IMU localization is unchanged for Webots
and other devices. Automatic charging/return is disabled only in the chosen
physical demo deployment: this flow stops at delivery and waits for another task.
Do not silently change the main warehouse configuration to enable this demo.

For two physical robots, use Simulation:Enabled=false and list both identities.
For one physical + one Webots robot, use Simulation:Enabled=true and explicitly
set Dispatch:AllowMixedRobotTypes=true. Keep one backend/navigation coordinator.
Warehouse, battery, station, presence, payload, maintenance and verification
eligibility rules are unchanged.

The current shared R01/R02 assets were registered as SIMULATION and their type is
immutable. Do not run physical and Webots controllers with the same robot topics.
Use a correctly registered PHYSICAL identity or prepare an isolated demo registry;
no existing identity/history has been rewritten by this change.

## Floor layout and routes

The earlier 3x3 photo was a size experiment, not the authoritative physical layout.
Use the actual connected tape and printed markers. Logical QR identities, links
and route order still must agree with the task; physical lengths need not match
Webots grid metres in marker-event mode. Existing Webots world/manifest validation
is retained. The metric scene exporter refuses a line-event profile: it cannot
invent physical dimensions or move simulation markers from logical label edits.

Select a route with the QR checkpoints that are actually printed along this line.
The controller does not invent camera hits for hidden transit nodes from the
separated-lane simulation map. The initial scanner supports printed 4x4 IDs 0..49.
Routes containing unprinted lane nodes are explicitly rejected.

Place the car facing from pickup toward delivery along the tape. No automatic
90-degree turn or U-turn is implemented in this straight demo. After a task the
same process can receive another forward task. An opposite-direction task is
rejected until the car is repositioned/reoriented and a new controller session
is started. This prevents blindly driving forward for a route behind the car.

## Intersection demo

Both contenders stop before the reserved intersection and use the existing
APPROACH / GO / ENTER ACK protocol. The center marker does not mean EXIT.
The outgoing checkpoint must be physically placed outside the crossing; after
that target is confirmed and the center has left the live camera frame, send
EXIT and retain ownership until its backend ACK. No encoder distance is used
for this marker-event demo. Placing an exit marker outside the occupied area is
an installation rule, not a claim of measured body-clearance certification.

The backend's existing real obstacle sensing and fresh traffic-grant gates
remain intact. The earlier no-LiDAR supervised keyboard test does not generate
fake CLEAR reports for task dispatch. Operator presence near a stopped car does
not prevent station verification polling; CRITICAL obstacles still block driving.

A virtual robot is not a physical obstacle visible to the JetRacer's LiDAR.
Mixed mode demonstrates shared traffic coordination on matching logical routes;
it does not claim a synchronized physical/virtual collision body.

## Display and safety

ARUCO_LINE_DEMO READY reports carry an ID, map revision, distinct-frame count
and stopped flag. Heading and reprojection error remain null. The live map
shows the last camera-confirmed QR; it does not fabricate smooth positions
between markers. Metric ARUCO_IMU_ODOMETRY visualization remains available in
the separate calibrated mode.

Losing the line, stale frames, MQTT/ACK failure, Pause or a watchdog stops motion.
The default forward command is 0.10 m/s with bounded steering correction.
Segments time out at 45 s; an independent 50 s motion bound and 0.35 s command
heartbeat guard remain. These are time limits, not destination-distance estimates.
Pause/Resume retains the same mission/load/traffic ownership and verifies local
stopped/moved feedback. Restart/cancel keeps an unfinished-mission recovery hold.
The source notebook is not overwritten or restarted automatically.

## Verification evidence

Automated tests cover line steering direction and loss, excluding printed-marker
polygons from tape detection, frame confirmation/reset, expected-ID-only progress,
both transfer gates, a second task in the same connected process, paused/critical
station presence, and center/exit traffic locks. Backend tests verify that missing
metric calibration is accepted only for explicitly enabled checkpoint-demo devices;
unknown/unlisted, unstopped, one-frame or fabricated-heading reports are rejected.
Existing strict metric and Webots regression tests remain enabled.

These are software tests. Since this adapter's initial validation, R03 has been
deployed on the Jetson, camera/line detection and stationary sensors have been
observed, real task attempts have reached pickup verification, and the navigation
ACK topic/bootstrap fixes have been checked through the broker without motion.
A complete physical QR6 → QR7 run and two-robot intersection acceptance remain
pending. See the home test guide and dated handoff validation for current scope.
