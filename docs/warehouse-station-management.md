# Warehouse and station management (FE-02)

Implemented against the replacement BA set `CAPSTONE-SMARTFLEET (6)..(12).pdf`,
not the retired five SWP490 reports. Relevant requirements are Report 1 FE-02,
Report 3 UC03 and BR07/08/47–52, and the Report 4 warehouse/zone/station model.
The additional safety and commissioning policies below make the requested
StationId-to-ArUco behavior explicit; they are not claims of physical certification.

## Label the commissioned Webots map

As Admin, open **Warehouse Maps**, choose **Edit Points** and select a QR
checkpoint. Enter its display name and operational role, then choose **Save to
Database**. A station record is optional. Under **Associated station**, select
an existing unmapped station or create one in place. **Save station** saves the
station record and its mapping. Station type controls its eligibility for
business operations. Use **Advanced warehouse administration** for warehouse,
zone and berth administration.

**Edit Links** toggles corridors between QR checkpoints. If a saved QR map
differs from the commissioned Webots markers, corridors or intersections, the
editor shows a warning and simulation dispatch stays blocked until the map
matches a commissioned scene. For an intentional physical change, commission
the world and update the matching anchors, corridors and intersections in the
scene manifest. For an unintended edit, restore the approved map.

### Optional cleanup of the saved legacy drawing

The inspected simulation map (MapID 1, revision 5) contains these legacy cells
at zero-based grid coordinates `(row, column)`.

| Points | Coordinates in point order |
| --- | --- |
| S1, S2, S3 | (6, 9), (6, 13), (6, 17) |
| S4, S5, S6 | (9, 9), (9, 13), (9, 17) |
| S7, S8, S9 | (12, 9), (12, 13), (12, 17) |
| CHG1 | (15, 13) |

The 13 candidate links are S1–S2, S2–S3, S4–S5, S5–S6, S7–S8, S8–S9,
S1–S4, S4–S7, S2–S5, S5–S8, S3–S6, S6–S9 and S8–CHG1. The simulation editor
filters this drawing from view, so storage cleanup is optional.

For a future storage cleanup, export the current map and its revision. Confirm
these exact cells still have their legacy types and labels and that every link
touching them stays within the listed set. Confirm each cell's replacement type
against the commissioned floor. Build a candidate map that changes only those
cells and links, and compare every QR cell, QR link and intersection flag before
and after. Validate the candidate against the current Webots scene in an isolated
copy, then use the revision-checked map save during an idle maintenance window.
The existing station records, mappings, reservations and mission history are
outside the map document.

## Set up and operate

1. Start the updated Compose stack with `docker compose up -d --build`.
   Its `db-init` applies the additive FE-02 patch on existing demo databases.
   For a separately managed PostgreSQL database, back it up and apply
   `Database/20261126_station_management.sql` after the previous four patches.
   Conflicting legacy identifiers cause a visible failure, not silent renaming.
2. Sign in as Admin and open **Warehouse & Stations** (`/warehouse-stations`).
   Create/edit warehouse and zone records, then station records. Deactivate rather
   than delete historical identities. The catalog is readable by all three roles;
   only Admin may change warehouse/zone/station configuration.
3. Choose the station's code, display name, zone, type, local metric coordinates,
   yaw, active state, verification payload and commissioned ArUco checkpoint.
   Active stations require active parents and a mapping. Active PICKUP/DELIVERY
   stations also require distinct, nonempty station verification QR payloads.
4. The isolated SIM preset commissions a TRANSFER station at every non-intersection
   QR (0, 2, 3, 4, 5, 6, 7, 8), including both pickup and delivery capability.
   P1 at QR2 and D1 at QR7 retain their identities and verification payloads; they
   are examples, not the only allowed task endpoints. QR1 is shown but disabled
   as a goods-transfer stop, and remains available for route transit.
5. Sign in as Operator and choose any two different enabled QR points in Create
   Task. Both lists show the map's QR markers, with disabled entries explaining
   reserved intersections or missing/inactive mappings. Only the selected
   StationIds are sent; the backend resolves and validates their QR checkpoints.
   A TRANSFER station can be used at either end, including QR7 → QR2.
6. In **Live tracking & handover**, confirm loading/unloading using the selected
   station's verification payload (and package code when required). Newly assigned missions use `QR2`/`QR7` at those points; the other shipped points use `QR0`, `QR3`, etc. Mission snapshots created before the code upgrade keep their original `P1`/`D1` expectations. These text
   payloads are separate from the camera's numeric ArUco navigation IDs. The stage
   is PICKUP or DELIVERY according to the mission, not according to a station's name.

Station coordinates are logical local metric metadata. They do not change a
physical marker's map row/column, pose or size. The selected marker's commissioned
map geometry remains the navigation authority. Label changes leave the map revision,
world and scene manifest unchanged. Physical commissioning remains a separate step:
map/world incompatibility still blocks simulation dispatch.

## Safety, consistency and capacity

- One station belongs to one zone and one warehouse. Codes are case-insensitive;
  station codes remain globally unique to match the existing schema. Zone codes
  and names are unique within their warehouse. Verification payloads are unique.
- Each station maps to at most one marker; a marker maps to at most one station
  per warehouse, including inactive stations. Unknown, duplicated, uncommissioned
  and reserved-intersection markers are rejected. Map saves cannot invalidate
  these mappings either.
- Station types are PICKUP, DELIVERY, TRANSFER, WAITING and CHARGING. TRANSFER
  explicitly supports both ends of a goods mission while keeping one StationId,
  one marker and one verification payload. PICKUP/DELIVERY remain available for
  dedicated business stations. WAITING/CHARGING are dedicated berths and do not
  automatically become transfer points.
- Updates use revision checks and serializable PostgreSQL transactions. Audit
  entries include actor, timestamp and before/after values. Concurrent requests
  claiming the same marker cannot both succeed.
- Any ACTIVE assignment in a warehouse blocks its catalog edits, including FAILED
  assignments holding a physical load. Complete/recover the assignment first.
  Queued, unassigned tasks re-resolve mappings before eligibility checks and again
  inside assignment; assigned missions retain their persisted route snapshots.
- Changing placement, type, zone or active state of an occupied checkpoint is
  blocked. Camera occupancy/corridor claims persist through heartbeat loss; offline
  status never proves clearance. Occupied/reserved station records cannot be edited.
- Initial waiting/charging capacity is **one robot per station**. Admin or
  Maintenance may explicitly reserve a berth for a same-warehouse robot with no
  active assignment. Reservations prevent dispatching that robot. Another robot
  cannot reserve the same berth; one robot cannot reserve multiple berths.
  Occupancy combines reservation, legacy occupied state and last camera/corridor
  claims. Release requires the reserved robot, reason, physical-clearance confirmation
  and no remaining camera occupation. An offline claim is not auto-released.
- Reservation does not drive a robot, dock it, charge a battery or clear a physical
  obstacle. Those controls are outside this initial capacity policy.
- This deployment has one navigation coordinator and one configured
  `Navigation:WarehouseId`. Catalog records can span warehouses; mapping/routing
  can only commission the currently selected warehouse. Multiple simultaneously
  navigating warehouse maps require separate coordinator support.

## API

`GET /api/v1/warehouse-catalog` returns warehouses, zones, stations, occupancy,
robots and navigation/map revision. Admin writes `POST`/`PUT` on `warehouses`,
`zones`, `stations` (PUT includes the identity in the URL). Updates supply the
last-read `revision`; a conflict returns 409. Invalid selections return 400,
unknown record 404, unauthenticated 401 and wrong role 403.

Admin/Maintenance use `POST .../stations/{id}/berth` with `robotId`, `release`,
`physicalAreaConfirmedClear` and `reason`. `POST /api/v1/transport-tasks` resolves
markers from `warehouseId`, `pickupStationId` and `deliveryStationId` in QR navigation
mode. Optional legacy `pickupArUcoId`/`deliveryArUcoId` must equal the database mapping;
the legacy task-routing API cannot override a station mapping or an assigned route.

## Verification on 2026-11-26

- 129 backend tests passed; 34 frontend Playwright tests passed; TypeScript and
  production build passed. The existing Vite chunk-size advisory remains.
- 39 Python tests passed and simulation asset validation passed (nine anchors).
- An isolated Compose project `smartfleet-fe02` on port 8083 used real PostgreSQL,
  MQTT, backend and two Webots robots. Admin mapped P1 → QR2 / D1 → QR7; Operator
  submitted a station-only MANUAL task, assigned R01 and completed the camera/IMU
  mission with both FE07 confirmations persisted. The navigation map and scene
  compatibility were unchanged by station edits.
- A second station-only AUTO task completed on the updated Docker image, with
  measured robot movement and two persisted transfer confirmations. The actual
  Docker UI showed P1/QR2 and D1/QR7 without independent marker selectors.
  Two independent browser sessions passed live 2D/3D viewing checks with no
  page errors or failed HTTP responses.
- Real API tests rejected intersection/unknown/duplicate mappings, mismatched
  legacy request markers, WAITING as pickup, wrong roles, stale revisions and
  active-mission edits. A map save making mapped QR2 an intersection was rejected.
- Two concurrent Admin creates for one marker accepted exactly one and rejected
  the other. The migration was reapplied successfully on the isolated PostgreSQL
  database; no reset was used.

Run `python tools/check_station_management.py --url http://127.0.0.1:8083` only on
a fresh isolated demo. It intentionally writes test catalog records and confirms
simulated transfers. Its `--api-only` mode adds concurrent mapping checks on an
already exercised, idle isolated installation. Evidence is written to
`SMARTFLEET_EVIDENCE_DIR` (default `test-results/simulation`). Neither command is
an operational database repair tool or a physical hardware acceptance test.


## Scope clarification: any eligible QR can be a task endpoint

The replacement BA's UC05/SC-04 describes selection of pickup and delivery stations;
its P1/D1 scenario is an example, not an exclusive QR2/QR7 restriction. The user's
2026-11-26 clarification explicitly requires selection across all QR points at both
ends, with QR1 visible but transit-only. Supporting this required an explicit
TRANSFER type because the previous schema and service validation allowed only one
pickup/delivery role per station. `Database/20261126_transfer_stations.sql` updates
the type constraint. Task creation, dispatch revalidation, recovery, map editing,
station labels and verification all retain the same station identity and mapping.

`deploy/commission-demo-qr-stations.sql` upgrades only the matching SIM preset, once,
with audit entries. It preserves the physical scene, saved marker geometry,
missions, verification payloads and existing station IDs. Active work/failed load
holds, conflicting mappings, inactive stations and dedicated waiting/charging
berths prevent automatic commissioning. Real warehouses still use explicit Admin
commissioning, independently of the packaged simulation preset.

Validation includes all 56 distinct ordered pairs among eight transfer points,
reverse-direction task creation, same-point rejection, reserved-intersection
blocking, role restrictions, and migration idempotency. Use the isolated Operator
browser acceptance tool with `--all-qr --create` to exercise QR0→8, QR7→2,
QR3→5, QR4→6 and AUTO QR8→0 without starting physical robot missions.


## Neutral point names and verification labels

The SIM preset's display names are `Transfer point QR2` and `Transfer point QR7`
in place of `Pickup Station P1` and `Delivery Station D1`. All eight transfer
points use the same neutral naming scheme. Task dropdowns show the point name;
route summaries use QR IDs. Pickup/delivery identifies the current mission stage.

`deploy/rename-demo-transfer-points.sql` is an idempotent, audited metadata-only
update of the two default names. It preserves stable StationIds/codes, printed
station verification payloads P1/D1, frozen mission expectations, mappings and
physical geometry. It also preserves user-customized names. The confirmation
form explicitly shows the point's QR ID, display name and station verification
code without filling or submitting the scan input. This keeps QR7 usable as a
pickup point while still verifying its existing D1 station label.

To validate metadata migration independently on a stopped isolated test stack:
`python tools/check_demo_station_upgrade.py --project smartfleet-station-qr-flow --rename-labels`.
The checks cover active assignments, unchanged barcode/route data, custom-name
preservation and repeated initialization.


## Unified verification codes (SIM preset v5)

The current station catalog uses `QR0`, `QR2` ... `QR8` as verification payloads,
independent of whether the point is pickup or delivery in a task. Stable station
codes P1/D1 are database identities; their canonical payloads are now QR2/QR7.
`deploy/unify-demo-verification-codes.sql` updates only those two catalog payloads,
records before/after audit data and refuses conflicting/customized payloads.
It never rewrites MissionExecutions, historical scans or verification results.

A mission freezes its pickup/delivery codes when assignment creates its durable
execution record. Already assigned/running/held missions retain their original
codes until completion/recovery. An unassigned queued task uses the new catalog
codes when it is eventually assigned. An assignment that started before the
cutover can retain the older snapshot; the authoritative value is always its
persisted execution record, not the task's creation date or today's catalog.

Mission API fields `pickupVerificationCode` and `deliveryVerificationCode` expose
that snapshot. Both verification screens display these values, with an explicit
legacy notice for P1/D1. They never derive a mission's expected code from mutable
catalog data or automatically fill/submit the scanner input. Missing snapshot
codes keep QR verification disabled until mission data is refreshed. Package,
physical-transfer, retry, idempotency, role and safety checks remain enforced.

For an application update, deploy the snapshot-aware backend and frontend in an
idle window before applying the catalog cutover; refresh old browser clients.
Do not restart a controller/backend merely to change codes while a robot carries
a load. The additive database cutover itself preserves active/failed load holds.
Printed station labels must correspond to the applicable configured code; Webots
ArUco navigation images are independent and are not regenerated by this change.


The live cutover regression uses a dedicated Compose test stack: an initial
legacy P1/D1 mission is assigned before the cutover, the migration runs while it
waits at pickup, and it completes using its immutable original codes. A second
mission at QR7 → QR2 then rejects D1 and completes with QR7/QR2. The harness
`tools/check_simulation_mission.py --manual --cutover-project <isolated-project>`
is restricted to a station test project on port 8086, never the operational
stack. Its cutover fixture requires old P1/D1 catalog payloads before assignment;
that fixture preparation is for disposable test data only. Mission checks now
read verification codes directly from the mission snapshot.
