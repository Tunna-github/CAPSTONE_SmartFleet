# Dual-lane charging validation — 26 November 2026

Implementation and setup: [dual-lane-charging.md](dual-lane-charging.md).

## Automated checks

- Backend: 162 tests passed, including four distinct berth reservations,
  the 19%/20% readiness boundary, ordered camera progress, generation checks,
  retained offline reservations, interrupted departure recovery, and bay-entry
  protection without an artificial two-hop ring deadlock.
- Frontend: 59 Playwright tests passed; TypeScript checking passed. These cover
  map metadata round trips, directional links, transport-point filtering,
  charging controls and the existing operations/verification screens.
- Python: 48 tests passed, including durable charging journals, controller
  recovery, dock relocalization, laser packet thresholds and wall-clock presence.
- Both packaged scenes passed manifest/world/asset validation. The new scene has
  71 markers, 109 ordered links and four capacity-one charging bays.
- Five PostgreSQL commissioning cases passed in disposable database clones:
  valid/idempotent upgrade, active assignment, failed-load hold, reserved berth
  and customized physical corridor. Unsafe upgrades were rejected.

## Actual Webots run

The isolated `smartfleet-charging-final` stack used real camera/IMU controllers,
MQTT navigation grants and the simulated laser. No synthetic waypoint/arrival
events or observer positions authorized movement or docking.

- R01 completed QR0 → QR6 and R02 completed QR4 → QR5, with pickup and delivery
  confirmation at the selected transport points.
- Both reached distinct charging bays QR67/QR68 and became READY after fresh
  stopped camera reports. An assignment attempted during return was rejected.
- Operator Pause worked; Operator resume was forbidden. Maintenance resumed
  only after an actual stop ACK and a physical-safety confirmation.
- AUTO then dispatched R01 from QR67 to collect at QR5 and deliver at QR0.
  Its previous berth remained reserved until a camera-confirmed exit.
- All 13 transport waypoints and both handovers completed. R01 then returned
  through ten charging waypoints and became READY at QR67; R02 stayed at QR68.
- The measured minimum separation in the recorded run was approximately 1.49 m.
  This sample is evidence for these routes, not a guarantee for every traffic
  arrangement. Four-robot allocation was tested in backend tests; the physical
  simulation run used two robots.

This was a **recovery-assisted** acceptance run. Earlier attempts exposed a
laser CLEAR packet mismatch, stretched telemetry intervals during slow
rendering and a dock-relocalization null-marker crash. After fixes, an explicit
isolated fixture placed stopped robots at their last camera-confirmed
checkpoints, retained their database holds, and required Maintenance recovery.
The final AUTO transport and complete return ran without another simulator
restart. Faults and old evidence were retained rather than hidden by resetting
the database.

Straight cruise used the requested 1.25 multiplier. Approach, final QR arrival
and turn speeds were unchanged. Unresolved QR loss stops the robot and raises a
fault; this implementation does not drive blindly through an occluded marker.

## Main local installation

The local Compose installation on port 8080 was backed up before upgrading from
bootstrap version 5 to 6. A post-upgrade comparison preserved all 8 tasks,
7 assignments, 7 missions, 7 execution records, 19 verification attempts,
13 accepted verifications, 46 QR scan records, 8 incidents, the original eight
station identities/payloads, and all robot operational statuses. Four charging
stations were added. R01's existing MAINTENANCE state was intentionally retained.

Read-only API checks passed for all three roles. Browser checks confirmed 2D
tracking, a loaded 3D scene, two independent viewers, persistence across internal
navigation, and rejection of simulator reset/pause commands from viewers.
No browser errors or failed requests were recorded in that check.

Private database backups, recovery fixtures and detailed evidence are stored
under `%LOCALAPPDATA%/SmartFleet/charging-validation`, outside Git. The isolated
test stack was stopped after the run; its volumes were retained. The main
installation uses the canonical checked-in world, not a recovery fixture.

Battery charging is simulated. Physical deployment still requires real charger
and BMS feedback; a Webots pose is not a device-safety confirmation.
