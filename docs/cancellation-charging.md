# Cancellation, charging recovery and speed validation

Validated locally on 27 November 2026 using Docker/Webots R2025a.

## Empty task cancellation

The charging profile no longer depends on a configured, fixed cancellation
Home QR. An unloaded, healthy robot before pickup finishes its already granted
current leg to a camera-confirmed checkpoint, stops and acknowledges cancellation.
This does not authorize a new leg, invent a QR arrival or bypass LiDAR/IMU,
distance limits or traffic grants. Pause and Force Cancel still stop immediately.
Uncertain localization or device health leaves a Maintenance hold.

A correlated, stationary return command fences the old mission at that checkpoint.
The assignment becomes CANCELLED and mission ABORTED while the robot remains BUSY.
A persisted charging job reserves a berth and returns the robot. The handoff
invalidates the previous idle localization and waits for a fresh camera READY;
it cannot build a charging route from the task's old departure QR.
Existing berth ownership survives a cancellation before departure. It is only
released after camera-confirmed departure, or reused by the return job.

Maintenance can return an inspected, empty, cancelled robot from a hold. A failed
attempt recovered to QUEUED also has a separate empty return action: it preserves
the FAILED assignment/mission and queued task. Physical safety, load accounting,
reason, assignment correlation, command ACK and fresh camera localization are
required. Historical loaded recoveries are excluded from this empty-return list.

An Operator may manually retry a failed task on the same robot once a new charging
cycle created after every failed attempt is READY, has a stopped report newer than
the failure and still owns its berth. Merely setting AVAILABLE, an old READY
cycle, an open failure, or a return still in progress does not qualify. All usual
presence, localization, maintenance, load, task hold and payload checks still run
at assignment. AUTO continues to exclude robots that previously failed that task.
This retry creates a new assignment/mission and retains the old FAILED records.

## Local recovery performed

Task 10 had already been cancelled/failed with R02 between QR64 and QR65 and no
camera-confirmed stop marker. A PostgreSQL dump and before/after snapshots were
saved outside Git under `%LOCALAPPDATA%/SmartFleet/charging-validation`.

The user requested a simulation recovery. The canonical scene now starts R02 at
charging marker QR67; R01 remains at QR0. Restarting the scene placed R02 there
explicitly. This was a simulation position recovery, not autonomous completion of
the old interrupted route. The new camera QR67 and actual controller stop ACK
were received before Maintenance released the charging return. Task 10 remains
QUEUED/MANUAL, its original assignment 9 and mission 9 remain FAILED, and R02 is
READY/AVAILABLE at CHG1. No old failure evidence or R01 maintenance state was erased.

QR1's transfer bay is now painted blue in the generated world. This is an explicit
world/manifest asset update, not an automatic effect of changing a logical label.
Marker coordinates, directed corridors and marker IDs were retained. Existing
installations must stop/recover work before restarting a changed simulation scene.

## Requested 4x and observed speed

The profile requests `SMARTFLEET_CRUISE_SCALE=4.0` (override with
`SIMULATION_CRUISE_SCALE`). Baseline cruise is 2.2 rad/s, so the request is 8.8 rad/s.
The robot model allows 6.67 rad/s and the existing software cap is 6 rad/s.
Effective cruise is therefore **6 rad/s, approximately 2.73x**, not full 4x.
Approach, final arrival and turning speeds retain their previous limits.

`telemetry.motion` distinguishes commanded wheel speed, measured encoder speed,
simulation-time speed and wall-time throughput. During the isolated cancel run:

- Peak measured left wheel speed: 5.937 rad/s for a 6 rad/s command.
- Median straight cruise: about 5.93 rad/s and 0.196 metres per simulation second.
- Median real-time factor: 0.269, yielding about 0.0525 metres per wall-clock second.

These renderer results are machine/load dependent. Docker was using Mesa software
rendering, and the main simulator also ran during the isolated test. Increasing
the motor request cannot by itself overcome slow rendering or short approach/turn
segments. Neither the motor model nor the safety cap was silently increased.

## Verification and limits

- 173 backend tests, 51 Python tests, TypeScript check and 8 targeted UI tests pass.
  Eight manual retry cases cover safe return, an old cycle, return in progress,
  missing stopped evidence/berth, open/new failures and an offline robot. The safe
  case also confirms AUTO cannot select the failed robot.
- Both legacy and 71-marker dual-lane world/manifest validation pass.
- `tools/check_cancel_charging.py` ran a real isolated Webots task: cancel between
  QR64 and QR65, camera stop at QR65, assignment CANCELLED / mission ABORTED, real
  return to QR67, DOCKED then READY. It used actual MQTT/encoder observations and
  no synthetic robot arrivals. The final idle-localization hardening was added
  after this physical run and is covered by backend regression tests; it was also
  deployed and checked with a stationary main charging recovery.
- The final drawer was inspected in dark/light and 390-pixel mobile layouts.
  Cancelled-state screenshots use a GET-only fixture; no robot commands were sent.
- Browser checks cover live 2D/3D, persistent viewing across navigation and two
  independent viewer sessions. These checks are simulation evidence, not a claim
  of real-device safety certification or exhaustive traffic coverage.

For a new isolated run, start a fresh Compose project on port 8089 and adjust the
checker project name if needed. Its fresh-mission guard deliberately rejects
reuse of an installation containing previous missions. Never delete operational
volumes to satisfy a test precondition.
