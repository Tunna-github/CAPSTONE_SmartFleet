-- Explicit physical scene commissioning. Stop backend + Webots first, back up DB.
-- Never call this for an arbitrary customer map. Historical mission snapshots stay intact.
\ir commission-demo-stations.sql
\set lane_map `cat /simulation/config/dual-lane-map.json`
\set legacy_map `cat /simulation/config/map.json`
BEGIN;
SELECT pg_advisory_xact_lock(490,20261127);
CREATE TEMP TABLE lane_preset(next_map jsonb,old_map jsonb) ON COMMIT DROP;
INSERT INTO lane_preset VALUES (:'lane_map'::jsonb,:'legacy_map'::jsonb);
DO $$
DECLARE saved jsonb; configured jsonb; next_map jsonb; point record; station_id integer; charging_zone integer; current_revision integer;
BEGIN
 IF EXISTS(SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=6) THEN RETURN; END IF;
 IF NOT EXISTS(SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=5) THEN RAISE EXCEPTION 'Complete SIM catalog upgrade before lane commissioning'; END IF;
 LOCK TABLE public."Robots",public."TaskAssignments",public."Stations",public."StationCheckpoints",public."StationBerths",public."ChargingJobs",public."MapConfigurations" IN SHARE ROW EXCLUSIVE MODE;
 IF NOT EXISTS(SELECT 1 FROM public."Warehouses" WHERE "WarehouseID"=1 AND "WarehouseCode"='SIM' AND "IsActive")
 OR EXISTS(SELECT 1 FROM public."TaskAssignments" WHERE "AssignmentStatus"='ACTIVE')
 OR EXISTS(SELECT 1 FROM public."StationBerths")
 OR EXISTS(SELECT 1 FROM public."ChargingJobs" WHERE "State"<>'COMPLETED') THEN
   RAISE EXCEPTION 'Cannot move simulation geometry with ACTIVE assignments, failed load holds or reserved berths. Finish/recover them first';
 END IF;
 SELECT "MapMetadataJson"::jsonb->'navigationMap' INTO saved FROM public."MapConfigurations" WHERE "WarehouseID"=1 AND "IsActive";
 IF pg_temp.demo_geometry(saved) IS DISTINCT FROM pg_temp.demo_geometry((SELECT old_map FROM lane_preset))
 THEN RAISE EXCEPTION 'Customized physical corridors require explicit engineering recommissioning'; END IF;
 -- Same physical legacy geometry, ignoring labels/revisions/neutral display names.
 SELECT jsonb_agg(jsonb_build_array((c.value->>'arucoId')::int,r.ordinality-1,c.ordinality-1,c.value->'markerYawDeg',c.value->'markerSizeM',c.value->'isIntersection') ORDER BY (c.value->>'arucoId')::int)
 INTO configured FROM jsonb_array_elements(saved->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'type'='qr';
 IF configured IS DISTINCT FROM (SELECT jsonb_agg(jsonb_build_array((c.value->>'arucoId')::int,r.ordinality-1,c.ordinality-1,c.value->'markerYawDeg',c.value->'markerSizeM',c.value->'isIntersection') ORDER BY (c.value->>'arucoId')::int)
 FROM lane_preset p,jsonb_array_elements(p.old_map->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'type'='qr')
 OR coalesce((saved->>'directedLinks')::boolean,false) THEN RAISE EXCEPTION 'Customized map: explicit engineering recommissioning required'; END IF;
 IF EXISTS(SELECT 1 FROM public."StationCheckpoints" p JOIN public."Stations" s ON s."StationID"=p."StationId" WHERE p."WarehouseId"=1 AND (p."ArucoId" NOT IN (0,2,3,4,5,6,7,8) OR s."StationType"<>'TRANSFER' OR s."IsOccupied"))
 THEN RAISE EXCEPTION 'Customized/occupied stations require manual review before geometry upgrade'; END IF;
 SELECT p.next_map INTO next_map FROM lane_preset p;
 current_revision := (saved->>'revision')::int+1;
 next_map := jsonb_set(next_map,'{revision}',to_jsonb(current_revision));
 UPDATE public."MapConfigurations" SET "IsActive"=false WHERE "WarehouseID"=1 AND "IsActive";
 INSERT INTO public."MapConfigurations" ("WarehouseID","MapVersion","MapName","MapSource","MapMetadataJson")
 VALUES(1,'simulation-dual-lane-v2','Separated lanes and four charging bays','MANUAL',jsonb_build_object('navigationMap',next_map,'sceneId','smartfleet-dual-lane-r2025a-v2')::text);
 UPDATE public."Stations" s SET "CoordX"=p.col*.5,"CoordY"=p.row*.5,"Revision"="Revision"+1,"UpdatedAt"=now()
 FROM (SELECT cp."StationId",r.ordinality-1 AS row,c.ordinality-1 AS col FROM public."StationCheckpoints" cp,
 jsonb_array_elements(next_map->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c
 WHERE cp."WarehouseId"=1 AND c.value->>'type'='qr' AND (c.value->>'arucoId')::int=cp."ArucoId") p WHERE s."StationID"=p."StationId";
 INSERT INTO public."Zones" ("WarehouseID","ZoneCode","ZoneName","ZoneType") VALUES(1,'CHARGE','Charging zone','CHARGING') RETURNING "ZoneID" INTO charging_zone;
 FOR point IN SELECT (c.value->>'arucoId')::int AS qr,r.ordinality-1 AS row,c.ordinality-1 AS col,
 row_number() OVER(ORDER BY c.ordinality) AS bay FROM jsonb_array_elements(next_map->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'operationalRole'='CHARGING'
 LOOP
   INSERT INTO public."Stations"("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload","Capacity")
   VALUES(charging_zone,'CHG'||point.bay,'Charging bay '||point.bay,'CHARGING',point.col*.5,point.row*.5,'QR'||point.qr,1) RETURNING "StationID" INTO station_id;
   INSERT INTO public."StationCheckpoints" VALUES(station_id,1,point.qr);
 END LOOP;
 INSERT INTO public."SystemAuditLogs"("ActionCategory","ActionName","EntityType","EntityID","Description")
 VALUES('WAREHOUSE','COMMISSION_DUAL_LANE_SCENE','Warehouse','1',jsonb_build_object('oldRevision',current_revision-1,'newRevision',current_revision,'sceneId','smartfleet-dual-lane-r2025a-v2','physicalGeometryChanged',true)::text);
 INSERT INTO public.smartfleet_demo_bootstrap VALUES(6);
END; $$;
COMMIT;
