# CAPSTONE_SmartFleet

SmartFleet coordinates warehouse transport tasks and robot fleets. The Webots
controller is a simulation adapter; `Robot code` contains JetRacer/ROS Melodic
prototype scripts. Neither the simulated run nor a legacy CIRCLE/line-follow
test constitutes pickup/delivery verification for production.

## Local setup

For a portable isolated demo with PostgreSQL, MQTT, two Webots robots, live 2D
tracking and shared 3D viewing, run `python tools/setup_demo.py` then
`docker compose up -d --build`. Open http://localhost:8080. See
[simulation deployment](docs/simulation-deployment.md) for accounts, LAN viewing,
deployment and map/world commissioning constraints.

Use .NET 10, Node.js and npm, PostgreSQL, and an MQTT broker. Install frontend
dependencies with `npm ci` inside `FrontEnd for SmartFleet`. The frontend uses
port 8443; the HTTPS backend profile uses 7166.

`SmartFleetBE/appsettings.json` is private local configuration. Start with
`SmartFleetBE/appsettings.example.json` and supply a database connection and JWT
key with User Secrets or environment variables. Never commit real credentials.
JWT keys must be at least 32 UTF-8 bytes. For example, from `SmartFleetBE`:

```powershell
dotnet user-secrets set ConnectionStrings:DefaultConnection "<your PostgreSQL connection string>"
dotnet user-secrets set Jwt:Key ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48)))
dotnet run --launch-profile https
```

The SQL sample is an anonymized reference snapshot, with disabled users and
commands. Never import it into an existing operational database. For an existing
database, review and apply the additive patches in order:

1. `Database/20261123_task_interventions.sql`
2. `Database/20261124_outbox_safety_commands.sql`
3. `Database/20261124_auth_uniqueness.sql`
4. `Database/20261124_mission_verification.sql`

The second patch allows multiple safety controls per assignment while retaining
uniqueness of its original mission command. It changes indexes, not task rows.
`tests/postgres_outbox_regression.sql` verifies the actual indexes using a temporary
table and rolls back the test.

Enable `Mqtt:EnableWorkers` only against the intended broker. Enable
`Navigation:Enabled` only with a compatible robot adapter. QR route execution
uses `DemoRoute:Enabled` and is restricted to Development or the isolated Simulation environment. Select
`Navigation:WarehouseId` explicitly if more than one warehouse is active.
See `docs/aruco-auto-localization.md` and `docs/warehouse-map-storage.md` for
camera/marker commissioning and map storage.

New QR tasks submit `pickupArUcoId` and `deliveryArUcoId` together with the task.
The task becomes visible to the AUTO dispatcher only after route setup succeeds.
Existing queued MANUAL pilot tasks may still use the separate QR configuration API.

Heartbeat timeout is configured by `Dispatch:PresenceTimeoutSeconds` (default
45). Missing mission-start acknowledgement uses
`Dispatch:AssignmentAckTimeoutSeconds` (default 30). Below 20% battery prevents
new assignments; below 10% during a task fails/holds it and queues a durable stop.
Failures retain the ACTIVE assignment until Maintenance physically recovers the
load. These timeouts are prototype defaults and must be confirmed for acceptance.

## Validation

```powershell
dotnet test tests/SmartFleetBE.Tests/SmartFleetBE.Tests.csproj
python -m unittest discover -s tests -p "test_*.py" -v
cd "FrontEnd for SmartFleet"
npx tsc --noEmit
npm run build
npm run test:web
```

Backend unit tests use EF InMemory, so they do not prove PostgreSQL constraints.
`MissionVerificationTests` can also run against PostgreSQL by setting
`SMARTFLEET_TEST_PG` to a private test connection string and filtering that class.
They create/drop schemas named `fe07_test_<generated GUID>` and never change
operational tables. The account must be allowed to create schemas. Do not put
the connection string in source, command arguments, or test output.
Playwright tests mock API responses, so they do not prove robot hardware or the
entire API/database/MQTT workflow. Use isolated integration data for those checks.
The 152 planned Report 5 cases require their own requirement mapping and evidence.

## Pickup/delivery verification (FE07)

BA references: Report 1 page 7 (FE07), Report 3 page 7 (Operator UC06) and
BR28-31/87-89, Report 4 page 11 (waypoints, verification and scan events).
Operator confirms the physical transfer; navigation ArUco detection alone never
counts as loading/unloading or completes a mission.

Apply patch 4 before running this backend/controller version. Configure distinct,
nonempty `Stations.QrCodePayload` values for the pickup/delivery stations; select
navigation markers outside reserved intersections. The selected station QR payloads,
optional package code and route are snapshotted when assigning the mission.
Station-to-navigation-QR commissioning remains a warehouse setup responsibility.

In **QR Mission Monitor**, the robot stops at `AT_PICKUP`. Warehouse Operator
uses a keyboard/USB QR scanner to enter the station payload, scans the package code
when the task specifies one, and confirms the correct goods are loaded. Successful
verification permits `NAVIGATING_TO_DELIVERY`. At `AT_DELIVERY` it waits for the
unloading confirmation. Only after both verifications and the robot's final
completion event may the task/assignment complete and the robot become available.
Admin may inspect but cannot verify under UC06. Manual verification is available
to Operator with a mandatory reason and physical-transfer confirmation.

`GET /api/v1/missions` and `GET /api/v1/missions/{id}` read persisted progress and
history. Operator submits `POST /api/v1/missions/{id}/verifications` with
`requestId` (UUID), `stage` (`PICKUP`/`DELIVERY`), `type` (`QRCODE`/`MANUAL`),
`scannedQr`, `scannedPackageCode`, `physicalTransferConfirmed`, and optional `note`.
Retry a lost response with the **same UUID and body**. Mismatch returns 422 and
records the attempt; phase/hold/freshness/retry-limit conflicts return 409.
Default `Verification:MaxQrAttempts` is 3 (configurable 1-20); this is an explicit
prototype policy because BA names the retry rule without setting its number.

`MissionExecutions` stores the frozen route/expectations/cursor;
`MissionWaypoints` stores ordered arrivals; `RobotQrScanEvents` separates
navigation scans from pickup/delivery scans. `VerificationAttempts` is append-only;
`DeliveryVerifications` stores each final accepted stage. Status/audit history
records the operator, time and robot state. A wrong scan never frees a load.

The controller polls with a fresh camera-confirmed stopped-robot report. Reports
older than 15 seconds, reports from before this backend process, offline/faulted
robots and safety-held tasks cannot authorize a transfer. A backend restart keeps
the DB history; a changed navigation epoch or controller reconnect still stops
active navigation for Maintenance recovery. Persistence does not authorize blind
automatic motion after a restart. Legacy missions without a frozen route require
recovery/requeue; historical COMPLETED rows are preserved without fabricating proof.

Deploy the matching `Webots_Full_Mission_Controller` driver/module/calibration into
the Webots project and reload the world. The legacy JetRacer CIRCLE adapter does
not implement this handshake and still needs its separate hardware integration.
