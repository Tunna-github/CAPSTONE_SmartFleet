# Task cancellation and robot safety (Flow 4)

## Baseline verified

Before editing, local HEAD and GitHub main were both `577844564dac1bc408513ce2cb6e27a4edbf03c6`.
The old frontend cancellation only changed React state. The backend had no operational
cancellation endpoint; DELETE accepted only PENDING tasks without operational dependencies.
The robot accepted EXECUTE_TASK only. Mission failure previously ended the assignment.

## Responsibilities and state consistency

- Warehouse Operator owns business cancellation: direct cancel or request Maintenance recovery.
- Maintenance Technician owns force cancellation and physical safety/load confirmation.
- Every authenticated role can request a software Pause for an active task.
- Administrator does not implicitly acquire the Operator/Maintenance business permissions.
  There is currently no Supervisor role in the project's role model.
- Pause preserves task and mission status, ACTIVE assignment, and navigation resource locks.
- Unloaded direct cancellation is restricted to INITIALIZING/NAVIGATING_TO_PICKUP with
  fresh telemetry (30 seconds), online/BUSY robot, no serious incident, and no maintenance.
  Verified pickup, reached pickup, unknown demo progress after restart, delivery movement,
  offline state, device fault, or paused state require request/force recovery.
  A business reason cannot override these safety checks.
- Pending/queued/unassigned tasks cancel immediately and remain in history.
- Assigned direct cancellation changes task to CANCELLED, queues CANCEL_STOP, and retains
  ACTIVE assignment/BUSY robot. Only a correlated HOME_REACHED acknowledgement can release it.
- Request Cancel preserves the task, latches Pause, and queues PAUSE_TASK.
- Force Cancel requires a saved Operator request and Maintenance confirmation of both
  physical stop and load removal/accounting. Task becomes CANCELLED, mission ABORTED,
  assignment CANCELLED, robot MAINTENANCE (never automatically AVAILABLE).
- Mission failure now retains ACTIVE assignment while the robot is ERROR, preventing a
  ghost load from losing its physical resource lock before Maintenance recovery.

## Recover & Requeue

Maintenance can recover a failed/interrupted task without cancelling the order.
This requires an ACTIVE assignment, a failed task/mission, an ERROR robot, or an
Operator cancellation request. COMPLETED/CANCELLED tasks cannot be resurrected.
Maintenance must explicitly confirm all three: physical stop, removal/accounting of
the load, and return of that load to the task's ORIGINAL pickup station. The pickup
must still be active in the task's warehouse. The request includes expectedAssignmentId
to reject stale confirmations; UI checkboxes are scoped to each assignment.

Recovery atomically ends the old assignment as FAILED, preserves the failed mission
and its fault history, keeps the old robot in MAINTENANCE, and queues a stop/fence
command to that robot. The SAME task becomes QUEUED; AssignedAt/StartedAt are reset.
The safety overlay returns to NORMAL for a new attempt, not for reuse of the old robot.
Audit includes Maintenance user, physical confirmations, reason, and pre-recovery state.

ScheduledTime is set to at least recovery time + 30 seconds (a later existing schedule
is preserved). No extra migration is needed beyond TaskInterventions. AUTO tasks become
eligible at that time and are assigned on the worker's next polling cycle (default 5s).
No eligible replacement means the task remains QUEUED and is retried on later cycles.
MANUAL tasks retain their mode and need manual assignment after the cooldown.
Both dispatch paths reject safety holds, respect the scheduled time, and exclude every
robot that previously FAILED this task, even if it was restored to AVAILABLE.
Each retry creates a NEW assignment/mission; old records remain intact. A late
MISSION_STARTED for a terminal old mission is rejected, never ACKed as executable.

The Robot safety panel exposes Recover & Requeue to Maintenance. The task list now
preserves FAILED instead of coercing it to PENDING, includes a FAILED filter/badge,
and polls while visible to display requeue/redispatch changes.

API: POST `/api/v1/task-interventions/{taskId}/recover-requeue` (Maintenance only).
Body: reason, expectedAssignmentId, physicalSafetyConfirmed=true,
loadRemovedConfirmed=true, loadReturnedToPickupConfirmed=true.

## Durable protocol and audit

TaskInterventions is a persistent overlay, avoiding changes to existing status constraints.
Control commands use the existing transactional MQTT outbox and include taskId,
assignmentId, missionId, commandId, and increasing generation per task.
Publishing to MQTT is not a robot acknowledgement. Controls retry until a correlated
CONTROL_STOPPED/HOME_REACHED event; superseded controls are not republished.
Webots rejects controls older than the latest generation and retains a disk safety latch
across restarts. Late normal mission events cannot clear an intervention.

Request Cancel, Force Cancel, Direct Cancel, Pause, and robot acknowledgements write
SystemAuditLogs. Operator/Maintenance entries contain JWT user ID, UTC timestamp,
reason, assignment/robot IDs and the robot/mission snapshot BEFORE the mutation.
Status histories are preserved; cancelling never deletes a task.

## Deployment prerequisites

1. Stop the old backend before deploying code. Apply
   `Database/20261123_task_interventions.sql` to the intended PostgreSQL database.
   This is an additive schema migration, not a sample data import.
2. Deploy the updated controller BEFORE issuing commands from the new UI. An old robot
   ignores cancellation commands; the backend will keep its assignment held.
3. For the EMPTY, SINGLE-ROBOT Webots pilot only, enable DemoRoute, keep Navigation
   disabled, and configure `Cancellation:HomeArUcoId` with the actual Home QR.
   Example environment variable: `Cancellation__HomeArUcoId=0` ONLY if QR0 really is Home.
4. Home routing requires a fresh camera-confirmed QR at the stop point, an existing
   QR graph path, and the updated Webots controller. An interrupted robot between
   markers, missing Home configuration, navigation coordination mode, or invalid route
   remains held for Maintenance; no position or arrival is fabricated.
5. The physical JetRacer line follower has no validated Home-route planner. It implements
   persistent software stop only and remains held for physical recovery. Automatic
   physical return needs a calibrated localization/navigation adapter and hardware tests.
6. No automatic resume is introduced. After a Pause, Operator requests cancellation and
   Maintenance performs recovery. Restore robot availability through the existing
   Maintenance workflow only after the device/load is safe. Clear the controller's disk
   safety latch only as part of that physical recovery, with the old mission worker stopped.

These MQTT buttons are software controls, not a certified hardware E-Stop. For immediate
physical danger use the device's physical stop. UI explicitly distinguishes pending
commands from robot acknowledgement. No robot commands were sent during automated tests.

## API and UI

- GET `/api/v1/task-interventions`: active assignments and their safety overlays (all roles).
- POST `/api/v1/task-interventions/{taskId}/cancel`: Operator.
- POST `/api/v1/task-interventions/{taskId}/request-cancel`: Operator.
- POST `/api/v1/task-interventions/{taskId}/force-cancel`: Maintenance.
- POST `/api/v1/task-interventions/{taskId}/pause`: all authenticated roles.

POST body: reason (1..400 chars); Force Cancel additionally requires
physicalSafetyConfirmed=true and loadRemovedConfirmed=true.
The shared Robot safety panel appears on every authenticated page. Existing task Cancel
now calls the backend and refreshes persisted data instead of releasing a mock robot.

## Verification

- Backend: `dotnet test tests/SmartFleetBE.Tests` (30 passing tests).
  EF InMemory tests cover policy and state/audit transitions; they do NOT prove PostgreSQL
  constraints, SERIALIZABLE concurrency, real JWT authentication or physical motion.
- Robot: `python tests/test_robot_safety.py` (6 passing tests) executes actual safety functions with mocked
  motors/camera/MQTT. Both edited Python controllers also pass AST syntax validation.
- Frontend: `npm run build`, `npx tsc --noEmit`, and `npm run test:web`
  (5 passing Playwright tests in Edge, covering roles, recovery confirmation and FAILED display), with
  intercepted HTTP responses; these do not contact the real backend, DB, or broker.
- Live PostgreSQL migration, full Webots motion, and JetRacer hardware validation remain
  deployment/integration checks. Never infer physical stop from a successful HTTP response.
