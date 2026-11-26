# Requeue or delete a cancelled transport task

Warehouse Operators can open Transport tasks, filter CANCELLED and choose:

- **Requeue**: enter a reason and confirm the goods are available at the original
  pickup point. The same task returns to QUEUED. MANUAL/AUTO mode and any later
  scheduled time are retained; the earliest new dispatch is 30 seconds later.
  Current-attempt cancellation/failure timestamps are cleared. Previous status,
  mission, verification and audit records remain unchanged.
- **Delete**: confirm removal from the operations list. This is a logical deletion
  for CANCELLED tasks: all mission, verification, assignment and audit history is
  retained for reporting and investigations. It cannot be requeued afterwards.
  The existing policy for deleting a PENDING task is unchanged.

Both actions reject active assignments, nonterminal missions, a pending cancel/
return handoff, or a cancellation command without its ACK. A task without any
assignment can be cancelled and requeued/deleted without a robot handshake.
Requeue also validates the original warehouse/stations and the current checkpoint
mapping, and requires inventory confirmation. It does not mark any robot AVAILABLE,
release a berth, move a robot or clear its device safety latch. Charging and
Maintenance recovery are separate device workflows.

Endpoints (Warehouse Operator role only):

```http
POST /api/v1/transport-tasks/{taskId}/requeue
Content-Type: application/json

{"reason":"Goods ready for another attempt","goodsAtPickupConfirmed":true}
```

```http
DELETE /api/v1/transport-tasks/{taskId}/cancelled
```

Requeue returns the retry time. Conflicts return 409 with a reason; missing/deleted
tasks return 404, and invalid requeue confirmations return 400. Each successful
action records the actor, task, timestamp and reason. Requeue records the goods
confirmation and preserved dispatch mode/schedule as well.

## Database and deployment

Apply `Database/20261127_cancelled_task_actions.sql` before running the updated
backend against a native database. It adds nullable `DeletedAt` and `DeletedBy`
columns to `TransportTasks` without deleting existing data. Docker's db-init
applies it on new and existing demo installations. Repository task lists/details
exclude logically deleted rows; historical mission/report queries retain them.
No Webots world/controller update is required for these two actions.

## Validation

- 16 backend regression cases cover cancellation closure/ACKs, role restriction,
  inventory confirmation, station validity, scheduling, unchanged device state,
  retained history and duplicate/repeated actions. Full backend suite: 189 passed.
- Three new browser tests cover Requeue confirmation, Delete confirmation and a
  rejected deletion while cancellation is still in progress. These and the five
  existing task/business regressions pass; TypeScript and Docker builds pass.
- `tools/check_cancelled_task_actions.py` checks real HTTP/PostgreSQL on a fresh
  isolated `smartfleet-cancel-actions` Compose project at port 8089. MANUAL/AUTO
  preservation, role rejection, unacknowledged cancellation, hidden deleted rows,
  retained historical verification/mission data and three action audits passed.
  Historical operational rows in that check are explicit database fixtures, not
  actual robot acknowledgements; the check sends no robot commands.
- Before applying the additive migration to the main local demo, a database dump
  was saved under `%LOCALAPPDATA%/SmartFleet/charging-validation`. No task was
  requeued or deleted on the user's main demo as part of this verification.

For a new isolated run, use the current backend/web images in a fresh Compose
project with port 8089, database, mqtt, db-init, backend and web services. Webots
is not required for this API/database test. The checker refuses a nonempty task
list; do not remove operational volumes to satisfy that precondition.
