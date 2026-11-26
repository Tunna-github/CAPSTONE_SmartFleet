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
4. For the acceptance example, edit P1 to QR2 and D1 to QR7. A fresh demo already
   seeds these exact mappings. An upgrade deliberately leaves historical stations
   unmapped until an Admin verifies their physical meaning. No legacy task or
   mission is rewritten by the SQL patch.
5. Sign in as Operator, create a task and select the warehouse, P1 and D1.
   The form displays P1 → QR2 / D1 → QR7 but sends only StationIds. The backend
   resolves the current database mappings, validates the graph, and freezes the
   route and transfer expectations when assigning a mission. The approach route
   depends on robot position/occupancy; pickup must be QR2 and delivery QR7.
6. In **QR Mission Monitor**, confirm simulated loading with payload `P1` and
   unloading with `D1` (plus package code if specified). ArUco2/7 are navigation
   identities, not the station QR verification payloads.

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
- Station types remain PICKUP, DELIVERY, WAITING and CHARGING. The current model
  has a single StationType and task validation depends on it. Dual-use stations
  therefore require a separate capability/model change and are deliberately not
  introduced here. WAITING/CHARGING cannot be chosen as task pickup/delivery.
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
