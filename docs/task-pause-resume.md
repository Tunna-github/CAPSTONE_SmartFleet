# Pause and resume the same mission

Open **Robot safety / Pause**, enter a reason, and select **Pause / Stop**.
After the robot acknowledges its stop, inspect the robot, path and current load.
Tick the safety confirmation and select **Resume task**.

Resume preserves the task, assignment, mission, waypoint progress, goods verification,
traffic ownership and station reservations. It does not cancel or requeue the task.
The robot remains stopped until the backend acknowledges its resume readiness.

The backend requires a live, healthy robot, sufficient battery, fresh clear LiDAR,
no maintenance/fault or map-switch hold, and the original controller pause context.
The controller checks the same session, mission, map, navigation ownership and
wheel/heading stability. Another Pause supersedes an outstanding Resume.
Controller restarts or firmware without retained-session Resume support require
Maintenance recovery; a new mission is never reconstructed from a Resume command.

Existing databases need `Database/20261127_task_resume.sql`. Docker database setup
and the Aiven configuration helper include this additive migration.

## Validation on 2026-11-27

- Backend: 216 tests passed.
- Python controller: 64 tests passed.
- Browser robot-safety: 6 tests passed, including all three roles and a loaded mission.
- TypeScript and production frontend build passed.
- Live Docker/Webots: task 20, assignment/mission 46 moved, acknowledged Pause,
  remained stopped, acknowledged Resume and visibly moved again with the same
  mission and unchanged pickup/delivery verification. The quick test did not wait
  for the full transport mission.

Repeat the opt-in live check only on an idle simulation host:

```powershell
$env:SMARTFLEET_EVIDENCE_DIR = Join-Path $env:LOCALAPPDATA 'SmartFleet\aiven-migration\task-resume-live'
node tools/check_task_resume.mjs --run-demo-test
```

This creates a labelled manual test task and leaves it running after Resume.
Private screenshots and a JSON report are saved outside Git in the evidence folder.
