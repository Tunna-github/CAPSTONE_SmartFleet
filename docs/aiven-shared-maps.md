# Aiven, independent warehouses and Admin world selection

Docker uses **smartfleet_shared** on the configured Aiven PostgreSQL service.
The original Docker dataset (users/password hashes, tasks, missions, execution
snapshots, verification attempts, incidents, maintenance and audit history) was
restored with its original IDs and sequences. Old Aiven tasks/users were not merged.
The original Aiven `defaultdb` and stopped local `smartfleet_database` volume remain
recovery sources. Custom-format dumps and checksums are kept outside Git under
`%LOCALAPPDATA%/SmartFleet/aiven-migration`. Private `.env` and `.aiven/ca.pem` stay ignored.

## Warehouse identity

- **Current Warehouse (Map 2)** keeps the original warehouse ID, code SIM, stations
  and business history. Its world has separated lanes, 9 transfer points and 4
  charging bays.
- **Warehouse 1 (Map 1)** is a separate warehouse with 8 independent transfer
  stations, the original Aiven 3×3 geometry and QR1 reserved for transit. Its new
  database ID is deliberately different from the existing warehouse ID; friendly
  warehouse/map names are shown in the web.
- The same navigation QR number may exist in both warehouses. Station IDs are
  distinct. Map 1 verification payloads use `WH1:QR2`, `WH1:QR7`, etc., preserving
  globally unique station barcodes. Current Map 2 payloads such as `QR2` remain
  unchanged. The handover panel shows the frozen payload for the actual mission.
- Each warehouse retains an active logical map and its own station/mapping rows.
  `MapConfigurations.IsActive` is scoped per warehouse; only one
  `MapDeploymentProfiles.IsSelected` is true for the running physical simulator.
  Switching does not overwrite another warehouse's stations or move markers on
  logical label edits.

Operators choose **Warehouse** when creating a task. Pickup/delivery choices come
only from that warehouse. Tasks can be created for the inactive warehouse and stay
QUEUED. Manual/AUTO assignment revalidates station mappings and only dispatches in
the warehouse whose world is running. Switching explicitly relocates the two
virtual R01/R02 assets to the selected warehouse, with an audit; existing mission
snapshots and task warehouse references are retained. Offline TEST01 is not moved.
This remains one shared simulator, not two warehouses simulated simultaneously.

## Select from the web

Sign in as **Administrator** → **Live tracking & handover** → choose **Map 1** or
**Map 2** above the viewer. Enter a reason, confirm the shared world change, then
click **Back up & switch map**. Operator/Maintenance can view the selection but
cannot submit a change; the API also enforces Admin authorization.

Progress survives backend restart: safety checks → verified full backup → actual
robot stop ACKs → stop previous world → activate warehouse/world → verify fresh
camera localization and measured poses for both robots. The page resumes polling,
refreshes 2D and reconnects an already-open 3D viewer. 2D/3D tabs only change the view.

Active/interrupted assignments, unfinished safety acknowledgements, physical
robots and unfinished charging returns block selection. A durable transactional
row lock coordinates Manual/AUTO dispatch with accepted requests: new assignment
cannot race a map change. Catalog, registration and logical map writes are held
while changing. Reusing a request UUID does not start a second world reset.

The host worker executes only the saved map1/map2 profiles and keeps the requesting
Admin ID in the audit. The backend has no Docker socket or browser-supplied shell
commands. Manifest/hash/anchor/directed-corridor checks remain in place. Runtime
warehouse binding changes the logical warehouse association, not physical geometry.
Original Maintenance incidents are never cleared by map selection.

## Host setup and start

Requirements: Docker Desktop with Linux containers, Python, the current Git branch,
private Aiven credentials/CA and application credentials matching the shared users.
Only **one backend/Webots host** may control this shared DB. Other users open that
host's web URL; they do not start a second backend or simulator.

After configuring Aiven (or `configure_aiven.py --join-existing` on a replacement
host), configure the map worker once:

```powershell
cd C:\CAPSTONE\CAPSTONE_SmartFleet
python tools/map_switch_worker.py --configure
# Safely update backend/images with no active work; pause charging and obtain ACKs first.
docker compose up -d --no-deps --no-build backend
.\tools\start_map_manager.ps1 -InstallStartup
```

`--configure` installs the queue/gate schema after a backup if needed and creates a
private host UUID. An existing pre-separation installation must apply
`Database/20261127_independent_map_warehouses.sql` with the backend/worker stopped,
a full backup and no unfinished work before running this version. This host is
already upgraded. Do not rerun data imports/seeding against Aiven.

Windows has a current-user Startup shortcut for the background worker. To start it
again, run `.\tools\start_map_manager.ps1`. On Linux run
`python tools/map_switch_worker.py` under the host service manager. The worker must
use the same repository/private `.env` and Docker project as the active backend.
A replacement host uses its own UUID. Configure DEMO_OPERATOR_PASSWORD and
DEMO_MAINTENANCE_PASSWORD to the existing shared service accounts; generating a new
local `.env` does not change passwords in Aiven.

Normal daily start is `docker compose up -d`, then open `http://localhost:8080`.
The private COMPOSE_FILE selects compose.yaml, compose.charging.yaml and finally
compose.aiven.yaml. Local database/db-init services are excluded; no cloud seeding
runs. The backend owns a singleton advisory-lock connection and stops if it loses
that connection. This prevents duplicate controllers; it is not HA failover.

CLI selection submits the same Admin request and waits for the worker:

```powershell
python tools/select_aiven_map.py --status
python tools/select_aiven_map.py map1
python tools/select_aiven_map.py map2
```

Rebuild the current profile only after finishing work and pausing open charging
cycles with real ACKs. After restart, inspect fresh camera localization before
Maintenance resume. Previous runtime volumes and pre-switch `.env` copies remain.

## Recovery and retained data

Before-world-stop failure leaves the map unchanged and any robot pause held. An
unverified world or worker crash leaves **FAILED_HOLD**; dispatch stays blocked.
Inspect the private map-switch log, backup, `.env` and actual world first. When the
intended world is already running correctly, a host operator can verify recovery:

```powershell
python tools/map_switch_worker.py --verify-recovery REQUEST_UUID
```

This checks the retained backup hash/archive, selected profile/warehouse, no active
assignments or charging cycles, virtual fleet association and fresh camera/pose
reports before releasing that request's gate. It does not reset device incidents.
If those checks fail, repair/recommission the host first; do not delete safety
volumes or force-clear the gate.

The local database container is stopped, with its volume retained. To roll back,
first stop/recover work, back up Aiven and stop backend/Webots, then restore the
private `env-before-aiven` and original runtime volumes. Operations made since
migration remain in Aiven and its dumps; they are not silently copied back locally.
DBeaver must choose **smartfleet_shared** for current data, rather than defaultdb.

## Capacity and validation

The supplied Aiven Console showed 1 GB storage (14% used), 86% RAM and a service
connection limit of 20. Current shared data is about 12 MB. World assets/rendering
stay on Docker. The backend pool is capped at 4 plus its owner connection; the
worker uses short-lived CLI connections. Current data fits the small demo quota;
this is not a production load or high-availability certification.

Private evidence includes before/after dumps and original-row comparisons. All
pre-separation users, tasks, assignments, missions, execution/verification history
and 13 original stations/mappings were retained unchanged. Warehouse 1 adds new
station rows. Named validation tasks are separate and retain their audit history.

Completed checks include Admin-only authorization, invalid/stale selection,
repeated request IDs, active-mission rejection, blocked dispatch during switching,
backup verification and actual Map 1/Map 2 world/2D/3D viewing. After warehouse
separation, both catalogs and cross-warehouse/inactive-world rejection are tested.
The user's requested quick dispatch test observes actual robot movement without
waiting for a full transport mission. Backend: 203 tests; Python: 60 tests;
shared-map/station UI: 18 tests. Full test evidence remains outside Git.
