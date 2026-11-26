-- One-time upgrade of the shipped SIM preset, never a production commissioning rule.
-- Preserve customized catalogs, active work, map geometry and existing mappings.
\if :{?demo_map_json}
\else
\set demo_map_json `cat /simulation/config/map.json`
\endif
BEGIN;
SELECT pg_advisory_xact_lock(490, 20261126);
CREATE TEMP TABLE demo_station_preset (map jsonb) ON COMMIT DROP;
INSERT INTO demo_station_preset VALUES (:'demo_map_json'::jsonb);
CREATE OR REPLACE FUNCTION pg_temp.demo_geometry(document jsonb) RETURNS jsonb LANGUAGE sql AS $$
 WITH markers AS (
   SELECT r.ordinality - 1 AS row, c.ordinality - 1 AS col, c.value AS cell
   FROM jsonb_array_elements(document->'cells') WITH ORDINALITY r
   CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c
   WHERE c.value->>'type' = 'qr'
 ), edges AS (
   SELECT DISTINCT least((a.cell->>'arucoId')::int,(b.cell->>'arucoId')::int) AS x,
                   greatest((a.cell->>'arucoId')::int,(b.cell->>'arucoId')::int) AS y
   FROM jsonb_array_elements(document->'links') edge
   JOIN markers a ON lower(a.cell->>'label') = lower(edge->>0)
   JOIN markers b ON lower(b.cell->>'label') = lower(edge->>1)
 ) SELECT jsonb_build_object(
   'markers', (SELECT jsonb_agg(jsonb_build_object('row',row,'col',col,'id',cell->'arucoId',
      'yaw',cell->'markerYawDeg','size',cell->'markerSizeM','intersection',coalesce(cell->'isIntersection','false'::jsonb))
      ORDER BY (cell->>'arucoId')::int) FROM markers),
   'corridors', (SELECT jsonb_agg(jsonb_build_array(x,y) ORDER BY x,y) FROM edges));
$$;
DO $$
DECLARE
 warehouse_id integer;
 saved_map jsonb;
 preset_map jsonb;
 changed record;
BEGIN
 IF to_regclass('public.smartfleet_demo_bootstrap') IS NULL THEN
   RAISE NOTICE 'Demo station upgrade skipped: database is not an isolated SmartFleet demo.'; RETURN;
 END IF;
 IF EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version = 2) THEN RETURN; END IF;
 -- Initialization normally runs before the backend; these locks also protect a live additive upgrade.
 LOCK TABLE public."Warehouses", public."Zones", public."Stations", public."StationCheckpoints",
   public."MapConfigurations", public."TaskAssignments", public."StationBerths" IN SHARE ROW EXCLUSIVE MODE;
 SELECT "WarehouseID" INTO warehouse_id FROM public."Warehouses"
   WHERE "WarehouseCode" = 'SIM' AND "WarehouseName" = 'SmartFleet simulation' AND "IsActive";
 IF warehouse_id IS DISTINCT FROM 1 THEN
   RAISE NOTICE 'Demo station upgrade skipped: shipped SIM warehouse is not present.'; RETURN;
 END IF;
 IF EXISTS (SELECT 1 FROM public."TaskAssignments" a JOIN public."TransportTasks" t ON t."TaskID"=a."TaskID"
   WHERE t."WarehouseID"=warehouse_id AND a."AssignmentStatus"='ACTIVE') THEN
   RAISE NOTICE 'Demo station upgrade deferred: active assignments/holds must be resolved first.'; RETURN;
 END IF;
 SELECT "MapMetadataJson"::jsonb->'navigationMap' INTO saved_map FROM public."MapConfigurations"
   WHERE "WarehouseID"=warehouse_id AND "IsActive";
 SELECT map INTO preset_map FROM demo_station_preset;
 IF saved_map IS NULL OR pg_temp.demo_geometry(saved_map) IS DISTINCT FROM pg_temp.demo_geometry(preset_map) THEN
   RAISE NOTICE 'Demo station upgrade skipped: physical QR geometry/corridors differ from the shipped scene.'; RETURN;
 END IF;
 IF (SELECT count(*) FROM public."Stations" s JOIN public."Zones" z ON z."ZoneID"=s."ZoneID"
   WHERE z."WarehouseID"=warehouse_id AND z."ZoneCode"='SIM' AND z."IsActive" AND s."IsActive"
     AND ((s."StationCode"='P1' AND s."StationType"='PICKUP' AND s."QrCodePayload"='P1')
       OR (s."StationCode"='D1' AND s."StationType"='DELIVERY' AND s."QrCodePayload"='D1'))) <> 2 THEN
   RAISE NOTICE 'Demo station upgrade skipped: station definitions were customized.'; RETURN;
 END IF;
 IF EXISTS (SELECT 1 FROM public."Stations" s LEFT JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
   WHERE s."StationCode" IN ('P1','D1') AND ((p."StationId" IS NULL AND s."Revision"<>1)
     OR p."WarehouseId"<>warehouse_id OR p."ArucoId"<>CASE s."StationCode" WHEN 'P1' THEN 2 ELSE 7 END
     OR s."IsOccupied" OR EXISTS (SELECT 1 FROM public."StationBerths" b WHERE b."StationId"=s."StationID")))
 OR EXISTS (SELECT 1 FROM public."StationCheckpoints" p JOIN public."Stations" s ON s."StationID"=p."StationId"
   WHERE p."WarehouseId"=warehouse_id AND p."ArucoId" IN (2,7)
     AND s."StationCode"<>CASE p."ArucoId" WHEN 2 THEN 'P1' ELSE 'D1' END) THEN
   RAISE NOTICE 'Demo station upgrade skipped: customized/occupied stations or conflicting mappings.'; RETURN;
 END IF;
 FOR changed IN
   INSERT INTO public."StationCheckpoints" ("StationId","WarehouseId","ArucoId")
   SELECT s."StationID",warehouse_id,CASE s."StationCode" WHEN 'P1' THEN 2 ELSE 7 END
   FROM public."Stations" s WHERE s."StationCode" IN ('P1','D1')
     AND NOT EXISTS (SELECT 1 FROM public."StationCheckpoints" p WHERE p."StationId"=s."StationID")
   RETURNING "StationId","ArucoId"
 LOOP
   UPDATE public."Stations" SET "Revision"="Revision"+1,"UpdatedAt"=now() WHERE "StationID"=changed."StationId";
   INSERT INTO public."SystemAuditLogs" ("ActionCategory","ActionName","EntityType","EntityID","Description")
     VALUES ('WAREHOUSE','COMMISSION_DEMO_STATION','Station',changed."StationId"::text,
       jsonb_build_object('source','demo-bootstrap-v2','previousArUcoId',null,'arucoId',changed."ArucoId",'mapRevision',saved_map->'revision')::text);
 END LOOP;
 INSERT INTO public.smartfleet_demo_bootstrap VALUES (2);
 RAISE NOTICE 'SIM stations commissioned: P1 -> QR2, D1 -> QR7. Scene, tasks and robot state unchanged.';
END; $$;
COMMIT;
