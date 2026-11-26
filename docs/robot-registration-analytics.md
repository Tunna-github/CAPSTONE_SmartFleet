# Robot registration and basic analytics (FE-03 / FE-08)

Base: `origin/main` commit `8ea7468` (FE-02 is included). Work continues on
`feat/task-cancellation`. Requirements use the replacement BA set
`CAPSTONE-SMARTFLEET (6)..(12).pdf`: Report 1 FE-03/FE-08, Report 3 UC04/UC10,
SC-12, BR04/45/46/54–59/99/100, Report 4 Robots/TaskAssignments/Missions,
and Report 5 FE-03/FE-08 scope. The additional user priority is registration,
real database counts and regression of the existing demonstration flows.

## Robot registration

Admin opens **Robot Registry** (`/admin/robots`). Register, search/list and configure
the warehouse, device code, PHYSICAL/SIMULATION type, model, control software, agent
version, optional IP/MAC, MQTT registration identifier, payload limit and active flag.
Operator and Maintenance cannot access the registry API or Admin editor.

A new robot is persisted OFFLINE with unknown battery, no heartbeat/telemetry and
no task assignment. Admin cannot fabricate ONLINE, battery or runtime state through
the registration endpoint. Existing dispatch checks still require active registration,
correct warehouse/type, fresh presence, adequate battery, no active work/fault and
task-specific localization/route/payload checks. Registration alone grants none of
those conditions. No dispatch/MQTT architecture or robot motion controller is replaced.

Device code/type/MQTT registration identity are immutable. Basic configuration edits
require a disconnected robot with no active assignment, charging or berth reservation.
FAILED assignments with an ACTIVE load hold remain protected. Deactivation and warehouse
changes also respect persistent camera/corridor occupancy. Existing commissioned history
prevents moving a robot to another warehouse. Deactivate instead of deleting history;
resolve incidents and maintenance before deactivation. Updates use RegistryRevision,
serializable transactions, audit entries and case-insensitive DB unique indexes.

SIMULATION registration is allowed only when `Simulation:Enabled` is true. It does
not spawn a new Webots robot, generate a world or rewrite the scene manifest. The
existing Webots agents continue using session-specific MQTT client IDs; the catalog
MQTT identifier is registration metadata, not broker authentication or agent provisioning.
Agents must publish their registered robot code using the existing transport contract.

## Analytics and dashboard

Admin/Operator open **Analytics & Reports** (`/admin/analytics` or
`/dashboard/analytics`). Maintenance is denied analytics and export APIs.
Admin/Operator dashboard summaries now use the same database analytics service,
with no sample trends, synthetic throughput or hardcoded priority counts.

Available data includes:

- Task counts by current status, completed/failed/cancelled, creation and completion counts.
- Registered, active, fresh online/offline, basic availability, low/unknown battery.
- Mission totals/statuses and failed missions still holding active assignments.
- Valid task/mission durations, maintenance/incident counts and assignment utilization.
- Date/warehouse/robot filters; basic daily bars/table; Excel/PDF exports sharing the
  filtered server aggregation and recording an export audit event.

Dates are inclusive local dates in **UTC+07**. The API converts them into a UTC
half-open interval. Default is seven days through today. Valid ranges start in 1970
or later, span at most 366 days and do not end in the future. Refresh runs every
five seconds while the page is visible. Errors remain visible; a failed refresh
does not replace previous data with fabricated zeros. Old data is labeled as the
last successful snapshot. Empty duration/success samples display N/A.

Task/mission status totals describe records **created** in the selected period and
their current states. `completedInPeriod`/daily completions instead use completion
timestamps, including work created earlier. Success rate is completed / (completed
+ failed), excluding cancelled tasks. Duration samples require valid timestamps.
Robot filtering includes tasks ever assigned to that robot, counted once per task.

Utilization is the union of assignment-held intervals, clipped to registration,
the selected period and now, divided by registered time in that period. It includes
failed load holds, and inactive robots remain in historical totals. This is not
measured motor movement, device uptime or energy consumption. Fleet availability is
a basic state count; task-specific eligibility still runs in existing dispatch.

Reports read one PostgreSQL repeatable-read snapshot. XLSX has typed values and four
sheets (Summary, Robot utilization, Daily, Definitions); stored text cannot become
a formula. PDF uses portable standard fonts and robot/warehouse IDs, with full device
codes in Excel. No new runtime NuGet/npm or Python reporting dependency is required
by the application containers.

## API and migration

- Admin: `GET/POST /api/v1/robot-registry`, `PUT /api/v1/robot-registry/{id}`.
- Admin/Operator: `GET /api/v1/analytics` and `/api/v1/analytics/export/{xlsx|pdf}`.
- Query parameters: `from=yyyy-MM-dd`, `to=yyyy-MM-dd`, optional `warehouseId`, `robotId`.
- Invalid input 400, wrong role 403, missing record 404, revision/identity/safety conflict 409.

Apply `Database/20261126_robot_registry.sql` after the five earlier additive patches
on separately managed databases, with a backup. Compose `db-init` applies it on both
fresh and existing demo installations. It adds registry revision, identifier indexes
and report query indexes; it does not reset robots/tasks or alter mission history.
Duplicate legacy identities fail visibly rather than being renamed automatically.

For the local demo after updating images: `docker compose up -d --build`. Never
restart an operational simulation mid-mission merely to demonstrate a UI change;
finish/recover active work first.

## Regression checks

Run backend tests, TypeScript, `npm run test:web`, Python controller tests and
`python tools/validate_simulation.py`. Mocked UI/unit tests are supplemented by
`python tools/check_registry_analytics.py --url http://127.0.0.1:8083 --write`
on an explicitly isolated writable Compose stack. It tests actual PostgreSQL/API
registration, duplicate/concurrent writes, revisions, authorization, filter validation
and report exports. Test registrations are deactivated and their audit history retained.

`tools/check_simulation_mission.py` accepts paired `--pickup-station-id` and
`--delivery-station-id`, plus `--manual --robot-code R02` for an explicit robot.
These options select existing commissioned stations, not independent navigation QR IDs.
Use isolated fixtures and Operator confirmations only for simulated handling tests.

## Verified on 2026-11-26

- 143 backend tests, 44 mocked frontend tests and 39 Python tests passed;
  TypeScript, Docker backend/frontend builds and nine-anchor asset validation passed.
- Real PostgreSQL/API checks covered new-device OFFLINE/unknown battery, identity
  uniqueness including concurrent requests, revision protection, role restrictions,
  date bounds and both exports. Excel counts matched the API snapshot and all PDF
  pages were parsed and rendered for inspection.
- Real browser Login passed for all three roles, rejected a wrong password, opened
  Admin registry and Admin/Operator analytics, and denied Maintenance those Admin
  configuration/analytics screens. No page or server errors were observed.
- Actual AUTO R01 runs completed, with measured movement and persisted pickup/delivery
  verification. Independent browser sessions viewed the same 3D scene; the 2D map
  showed measured motion for both R01 and R02.
- Regression exposed a reproducible native software-renderer stall on the R02 QR8 → QR7
  leg. Both robots and the observer stopped publishing; backend health handling
  correctly failed/held the task. Limiting rendering to two threads did not resolve
  it. A single-thread/128-bit LLVMpipe run completed the leg and both verifications.
  Compose now supplies that renderer configuration without changing dispatch/MQTT,
  controllers, world geometry or safety timeouts. Failed test databases were retained.
- A subsequent MANUAL R02 run with P1 → QR2 and D1 → QR7 also completed under
  the serial/128-bit configuration. Both transfer confirmations persisted; the live
  map showed measured movement and two 3D browser sessions remained functional.
  A real map write/restore test passed afterward, including blocked dispatch during
  a deliberate map/world mismatch. All temporary simulator stacks were stopped.

These checks cover the implemented features and simulated demonstrations, not every
planned BA test or physical JetRacer acceptance. The native graphics workaround still
needs a longer soak test on each target host before relying on unattended simulation.
