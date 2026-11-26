-- Complete the isolated SIM preset: every non-intersection QR is a transfer point.
-- The shared geometry check also performs the earlier P1/D1 legacy upgrade.
\ir commission-demo-stations.sql
BEGIN;
SELECT pg_advisory_xact_lock(490, 20261126);
CREATE TEMP TABLE demo_transfer_preset(map jsonb) ON COMMIT DROP;
INSERT INTO demo_transfer_preset VALUES (:'demo_map_json'::jsonb);
DO $$
DECLARE
 saved_map jsonb;
 point record;
 owner record;
 target_id integer;
 code text;
BEGIN
 IF to_regclass('public.smartfleet_demo_bootstrap') IS NULL THEN RETURN; END IF;
 IF NOT EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=2)
   OR EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=3) THEN RETURN; END IF;
 LOCK TABLE public."Warehouses",public."Zones",public."Stations",public."StationCheckpoints",
   public."MapConfigurations",public."TaskAssignments",public."StationBerths" IN SHARE ROW EXCLUSIVE MODE;
 IF NOT EXISTS (SELECT 1 FROM public."Warehouses" WHERE "WarehouseID"=1 AND "WarehouseCode"='SIM' AND "IsActive")
   OR NOT EXISTS (SELECT 1 FROM public."Zones" WHERE "ZoneID"=1 AND "WarehouseID"=1 AND "ZoneCode"='SIM' AND "IsActive")
   OR EXISTS (SELECT 1 FROM public."TaskAssignments" a JOIN public."TransportTasks" t ON t."TaskID"=a."TaskID"
     WHERE t."WarehouseID"=1 AND a."AssignmentStatus"='ACTIVE') THEN
   RAISE NOTICE 'QR transfer upgrade deferred: SIM is customized or has active assignments/holds.'; RETURN;
 END IF;
 SELECT "MapMetadataJson"::jsonb->'navigationMap' INTO saved_map FROM public."MapConfigurations" WHERE "WarehouseID"=1 AND "IsActive";
 IF saved_map IS NULL OR pg_temp.demo_geometry(saved_map) IS DISTINCT FROM pg_temp.demo_geometry((SELECT map FROM demo_transfer_preset)) THEN
   RAISE NOTICE 'QR transfer upgrade skipped: map differs from the shipped scene.'; RETURN;
 END IF;
 -- Preflight the entire set before changing anything. Never repurpose dedicated berths.
 FOR point IN SELECT c.value AS cell FROM jsonb_array_elements(saved_map->'cells') r(value)
   CROSS JOIN LATERAL jsonb_array_elements(r.value) c(value) WHERE c.value->>'type'='qr' AND NOT coalesce((c.value->>'isIntersection')::bool,false)
 LOOP
   SELECT s.* INTO owner FROM public."Stations" s JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
     WHERE p."WarehouseId"=1 AND p."ArucoId"=(point.cell->>'arucoId')::int;
   code := 'QR'||(point.cell->>'arucoId');
   IF FOUND THEN
     IF owner."StationType" NOT IN ('PICKUP','DELIVERY','TRANSFER') OR NOT owner."IsActive" OR owner."IsOccupied"
       OR nullif(btrim(owner."QrCodePayload"),'') IS NULL
       OR EXISTS (SELECT 1 FROM public."StationBerths" WHERE "StationId"=owner."StationID") THEN
       RAISE NOTICE 'QR transfer upgrade skipped: dedicated, inactive or occupied station at %.',code; RETURN;
     END IF;
   ELSIF EXISTS (SELECT 1 FROM public."Stations" WHERE lower(btrim("StationCode"))=lower(code) OR "QrCodePayload"=code) THEN
     RAISE NOTICE 'QR transfer upgrade skipped: station code/payload % is already in use.',code; RETURN;
   END IF;
 END LOOP;
 FOR point IN SELECT c.value AS cell,r.ordinality-1 AS row_index,c.ordinality-1 AS col_index
   FROM jsonb_array_elements(saved_map->'cells') WITH ORDINALITY r
   CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c
   WHERE c.value->>'type'='qr' AND NOT coalesce((c.value->>'isIntersection')::bool,false)
 LOOP
   SELECT s.* INTO owner FROM public."Stations" s JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
     WHERE p."WarehouseId"=1 AND p."ArucoId"=(point.cell->>'arucoId')::int;
   code := 'QR'||(point.cell->>'arucoId');
   IF FOUND THEN
     target_id := owner."StationID";
     IF owner."StationType"='TRANSFER' THEN CONTINUE; END IF;
     UPDATE public."Stations" SET "StationType"='TRANSFER',"Revision"="Revision"+1,"UpdatedAt"=now() WHERE "StationID"=target_id;
   ELSE
     -- SIM metric coordinates use 0.5 metres/grid cell. This does not move a Webots object.
     INSERT INTO public."Stations" ("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload")
       VALUES (1,code,'Transfer point '||code,'TRANSFER',point.col_index*.5,point.row_index*.5,code) RETURNING "StationID" INTO target_id;
     INSERT INTO public."StationCheckpoints" VALUES (target_id,1,(point.cell->>'arucoId')::int);
   END IF;
   INSERT INTO public."SystemAuditLogs" ("ActionCategory","ActionName","EntityType","EntityID","Description")
     VALUES ('WAREHOUSE','COMMISSION_QR_TRANSFER','Station',target_id::text,
       jsonb_build_object('source','demo-bootstrap-v3','arucoId',point.cell->'arucoId','previousType',owner."StationType",'type','TRANSFER')::text);
 END LOOP;
 INSERT INTO public.smartfleet_demo_bootstrap VALUES (3);
 RAISE NOTICE 'SIM transfer points ready at all non-intersection QR markers. Reserved intersections remain transit-only.';
END; $$;
COMMIT;
