# Named transfer areas on the separated-lane scene

| Checkpoint / confirmation payload | Area name |
| --- | --- |
| QR0 | Dairy Area |
| QR1 | Instant Noodles |
| QR2 | Snacks & Sweets |
| QR3 | Beverages |
| QR4 | Rice & Cereals |
| QR5 | Condiments |
| QR6 | Canned Food |
| QR7 | Cleaning Supplies |
| QR8 | Household Goods |

All nine are TRANSFER stations, usable for pickup or delivery. A task still
requires different endpoints. QR1 is a dead-end spur attached to QR30, inside
its circulation ring. It does not serve through traffic in the separated-lane
scene. QR1 on the legacy scene remains a reserved intersection and is still
rejected as an endpoint.

The area name is a logical station label, not a replacement for the marker ID
or barcode. Task dropdowns show `Instant Noodles · QR1`, for example. Map labels
retain QR IDs and show the English name beneath them. Frozen mission routes,
station IDs, historical confirmations and existing barcode payloads are not
renamed. The new QR1 station uses the confirmation payload `QR1`.

`simulation/config/transfer-area-names.json` supplies the default names to the
map builder and SIM database commissioning. Version 7 checks the full physical
marker layout, ordered road graph, and QR1's two-link bay spur before changing
its role. It preserves world bytes, marker positions, textures and robot poses.
No simulation geometry is generated from a logical name edit.

For an existing installation, first complete active assignments/failed holds.
Pause unfinished charging jobs through Maintenance and wait for actual stop
ACKs. Back up the database, stop backend dispatch, apply db-init and recreate
the backend/web services. The unchanged simulator can remain running. After
fresh camera localization, Maintenance resumes each paused charging job with
physical safety confirmed. A new command generation adopts the new map revision
without releasing its berth or inventing a camera arrival. Do not restart
Webots with a parked/reserved robot merely to change its logical name.

Validation on 27 November 2026: 163 backend tests, 25 related UI tests,
TypeScript checking, six disposable PostgreSQL upgrade cases, actual docked
controller Pause/ACK/recovery to READY on revision 9, and browser selection of
QR1 at both ends. Backend route tests resolve StationIds and generate directed
routes from/to QR1. Browser checks opened the task form without submitting a
task and confirmed all nine English names plus QR labels on the live map.
No new physical transport mission to QR1 was run during this commissioning.
