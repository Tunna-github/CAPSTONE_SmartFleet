# CAPSTONE_SmartFleet

SmartFleet coordinates warehouse transport tasks and robot fleets. The Webots
controller is a simulation adapter; `Robot code` contains JetRacer/ROS Melodic
prototype scripts. Neither the simulated run nor a legacy CIRCLE/line-follow
test constitutes pickup/delivery verification for production.

## Local setup

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

The second patch allows multiple safety controls per assignment while retaining
uniqueness of its original mission command. It changes indexes, not task rows.
`tests/postgres_outbox_regression.sql` verifies the actual indexes using a temporary
table and rolls back the test.

Enable `Mqtt:EnableWorkers` only against the intended broker. Enable
`Navigation:Enabled` only with a compatible robot adapter. QR route execution
uses `DemoRoute:Enabled` and is restricted to Development. Select
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
Playwright tests mock API responses, so they do not prove robot hardware or the
entire API/database/MQTT workflow. Use isolated integration data for those checks.
The 152 planned Report 5 cases require their own requirement mapping and evidence.
