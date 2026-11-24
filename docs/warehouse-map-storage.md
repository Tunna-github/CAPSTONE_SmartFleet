# Warehouse map persistence

Administrator edits remain local in the frontend until **Save to Database** is clicked.
The frontend sends the complete 18 × 28 grid and links to `PUT /api/v1/navigation/map`.
The backend validates it, checks the current revision and intersection safety locks,
commits PostgreSQL, then updates the snapshot used by navigation and QR route planning.
A failed database write returns HTTP 503 and does not change the live map.
A stale revision returns HTTP 409; reload before editing again.

## Database storage

The existing active `public."MapConfigurations"` row for the warehouse stores:

- `MapMetadataJson.navigationMap`: revision, dimensions, cells, physical ArUco IDs,
  intersection flags and links.
- `MapMetadataJson.navigationMapUpdatedAtUtc`: the server save time.

Existing SLAM/image metadata and `MapVersion` are preserved. No schema migration is
required. If no active map exists, one MANUAL map record is created. On the first
startup only, the old `App_Data/navigation-state.json` map is imported when the
database does not yet contain `navigationMap`. Later startups load the database
map even if the local map mirror is missing or older.

This deployment still uses one backend process and one selected warehouse. With
multiple active warehouses, configure `Navigation:WarehouseId` explicitly; the
backend does not guess. Device speed settings and intersection recovery locks
continue to use the local state file. Keep that file when restarting a live pilot.

Read-only verification query:

```sql
SELECT "MapID", "WarehouseID", "MapVersion", "IsActive",
       "MapMetadataJson"::jsonb -> 'navigationMap' ->> 'revision' AS navigation_revision,
       "MapMetadataJson"::jsonb ->> 'navigationMapUpdatedAtUtc' AS saved_at,
       "MapMetadataJson"::jsonb -> 'navigationMap' AS navigation_map
FROM public."MapConfigurations"
WHERE "IsActive" = TRUE;
```

## Check whether QR routing is usable

`GET /api/v1/demo-routing/map/validation` returns the saved map revision,
`qrNodeCount`, `corridorCount`, `readyForRouting`, and explanatory `issues`.
The Warehouse Maps page shows this result after loading or saving a map.

QR task routing needs at least two distinct physical ArUco markers and connected,
horizontal/vertical QR corridors. Station labels such as S1–S9 by themselves do
not define QR nodes. `GET /api/v1/demo-routing/map/graph` shows the directional
graph used by the planner. Validation checks the graph, not the physical world:
the marker positions, road directions and robot heading calibration must also
match Webots before testing motion.

## Validation performed

- Backend tests cover legacy import, preserving existing metadata, database reload
  without a local file, revision conflicts, failed writes, invalid QR IDs,
  intersection flag persistence, and explicit warehouse selection.
- A persisted nine-marker test grid is reloaded and routes are generated for all
  72 distinct start/destination pairs.
- Frontend tests check explicit Save, returned revision/reload, failed-save feedback,
  and the warning for a map without QR markers.
- A separate local integration check saves the existing layout to PostgreSQL and
  reloads it through a fresh coordinator. No robot motion commands are published.
