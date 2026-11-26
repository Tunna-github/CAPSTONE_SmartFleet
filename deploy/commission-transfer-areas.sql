-- Logical commissioning of QR1's existing off-road bay. No world/marker moves.
-- Stop dispatch first; unfinished charging cycles must be paused and stop-ACKed.
\set area_names `cat /simulation/config/transfer-area-names.json`
\set area_map `cat /simulation/config/dual-lane-map.json`
BEGIN;
SELECT pg_advisory_xact_lock(490,20261127);
CREATE TEMP TABLE area_preset(names jsonb, map jsonb) ON COMMIT DROP;
INSERT INTO area_preset VALUES (:'area_names'::jsonb,:'area_map'::jsonb);
DO $$
DECLARE saved jsonb; expected jsonb; normalized jsonb; names jsonb; point record; owner record;
 station_id integer; map_id integer; old_revision integer; marker_path text[];
BEGIN
 IF EXISTS(SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=7) THEN RETURN; END IF;
 IF NOT EXISTS(SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=6)
  OR NOT EXISTS(SELECT 1 FROM public."Warehouses" WHERE "WarehouseID"=1 AND "WarehouseCode"='SIM' AND "IsActive")
 THEN RAISE EXCEPTION 'Transfer-area commissioning requires the SIM separated-lane scene'; END IF;
 LOCK TABLE public."TaskAssignments",public."Stations",public."StationCheckpoints",public."StationBerths",public."ChargingJobs",public."MapConfigurations" IN SHARE ROW EXCLUSIVE MODE;
 IF EXISTS(SELECT 1 FROM public."TaskAssignments" WHERE "AssignmentStatus"='ACTIVE')
  OR EXISTS(SELECT 1 FROM public."ChargingJobs" WHERE "State"<>'COMPLETED' AND ("State"<>'PAUSED' OR "StopAcknowledgedAt" IS NULL))
 THEN RAISE EXCEPTION 'Finish active tasks; pause charging robots and obtain actual stop ACKs before changing bay capabilities'; END IF;
 SELECT "MapID","MapMetadataJson"::jsonb->'navigationMap' INTO map_id,saved
 FROM public."MapConfigurations" WHERE "WarehouseID"=1 AND "IsActive";
 SELECT map, p.names INTO expected,names FROM area_preset p;
 IF saved IS NULL OR NOT coalesce((saved->>'directedLinks')::boolean,false)
  OR saved->>'markerDictionary'<>'DICT_4X4_250'
 THEN RAISE EXCEPTION 'The active map is not the commissioned separated-lane scene'; END IF;
 -- Only QR1's role/reserved flag may differ; all physical data and ordered links
 -- must match the shipped scene. Other real reserved intersections stay blocked.
 normalized := saved;
 FOR point IN SELECT r.ordinality-1 AS row,c.ordinality-1 AS col,c.value AS cell
  FROM jsonb_array_elements(saved->'cells') WITH ORDINALITY r
  CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'type'='qr'
 LOOP
  IF (point.cell->>'arucoId')::int=1 THEN
   marker_path := ARRAY['cells',point.row::text,point.col::text];
   normalized := jsonb_set(normalized,marker_path||ARRAY['isIntersection'],'false'::jsonb);
   normalized := jsonb_set(normalized,marker_path||ARRAY['operationalRole'],'"TRANSFER"'::jsonb);
  END IF;
 END LOOP;
 IF (SELECT jsonb_agg(jsonb_build_array((c.value->>'arucoId')::int,r.ordinality-1,c.ordinality-1,
      c.value->'markerYawDeg',c.value->'markerSizeM',c.value->'isIntersection',c.value->'operationalRole') ORDER BY (c.value->>'arucoId')::int)
   FROM jsonb_array_elements(normalized->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'type'='qr')
 IS DISTINCT FROM
  (SELECT jsonb_agg(jsonb_build_array((c.value->>'arucoId')::int,r.ordinality-1,c.ordinality-1,
      c.value->'markerYawDeg',c.value->'markerSizeM',c.value->'isIntersection',c.value->'operationalRole') ORDER BY (c.value->>'arucoId')::int)
   FROM jsonb_array_elements(expected->'cells') WITH ORDINALITY r CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE c.value->>'type'='qr')
 OR saved->'rows' IS DISTINCT FROM expected->'rows' OR saved->'columns' IS DISTINCT FROM expected->'columns'
 OR (SELECT jsonb_agg(e.value ORDER BY e.value::text) FROM jsonb_array_elements(saved->'links') e)
    IS DISTINCT FROM (SELECT jsonb_agg(e.value ORDER BY e.value::text) FROM jsonb_array_elements(expected->'links') e)
 THEN RAISE EXCEPTION 'Customized physical layout requires engineering review before enabling QR1'; END IF;
 IF (SELECT count(*) FROM jsonb_array_elements(saved->'links') e WHERE e.value->>0='QR1' OR e.value->>1='QR1')<>2
  OR NOT saved->'links' @> '[["QR1","QR30"],["QR30","QR1"]]'::jsonb
 THEN RAISE EXCEPTION 'QR1 must be the protected dead-end bay beside QR30, never a through-road stop'; END IF;
 IF (SELECT count(*) FROM jsonb_each_text(names))<>9 THEN RAISE EXCEPTION 'Nine area names are required'; END IF;
 FOR point IN SELECT (c.value->>'arucoId')::int AS qr,r.ordinality-1 AS row,c.ordinality-1 AS col
  FROM jsonb_array_elements(normalized->'cells') WITH ORDINALITY r
  CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY c WHERE (c.value->>'arucoId')::int BETWEEN 0 AND 8
 LOOP
  SELECT s.* INTO owner FROM public."Stations" s JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
   JOIN public."Zones" z ON z."ZoneID"=s."ZoneID" WHERE p."WarehouseId"=1 AND p."ArucoId"=point.qr AND z."WarehouseID"=1;
  IF FOUND THEN
   IF owner."StationType"<>'TRANSFER' OR NOT owner."IsActive" OR owner."IsOccupied"
    OR EXISTS(SELECT 1 FROM public."StationBerths" WHERE "StationId"=owner."StationID")
   THEN RAISE EXCEPTION 'Occupied or incompatible station at QR%',point.qr; END IF;
   station_id := owner."StationID";
   UPDATE public."Stations" SET "StationName"=names->>point.qr::text,"Revision"="Revision"+1,"UpdatedAt"=now() WHERE "StationID"=station_id;
  ELSE
   IF point.qr<>1 THEN RAISE EXCEPTION 'Missing pre-existing transfer station at QR%',point.qr; END IF;
   IF EXISTS(SELECT 1 FROM public."Stations" WHERE lower("StationCode")='qr1' OR "QrCodePayload"='QR1')
   THEN RAISE EXCEPTION 'QR1 station code or confirmation payload is already in use'; END IF;
   INSERT INTO public."Stations"("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload")
   VALUES(1,'QR1',names->>'1','TRANSFER',point.col*.5,point.row*.5,'QR1') RETURNING "StationID" INTO station_id;
   INSERT INTO public."StationCheckpoints" VALUES(station_id,1,1);
  END IF;
  normalized := jsonb_set(normalized,ARRAY['cells',point.row::text,point.col::text,'displayName'],names->point.qr::text);
 END LOOP;
 old_revision := (saved->>'revision')::int;
 normalized := jsonb_set(normalized,'{revision}',to_jsonb(old_revision+1));
 UPDATE public."MapConfigurations" SET "MapMetadataJson"=jsonb_set("MapMetadataJson"::jsonb,'{navigationMap}',normalized)::text WHERE "MapID"=map_id;
 INSERT INTO public."SystemAuditLogs"("ActionCategory","ActionName","EntityType","EntityID","Description")
 VALUES('WAREHOUSE','COMMISSION_TRANSFER_AREAS','Warehouse','1',jsonb_build_object('oldRevision',old_revision,'newRevision',old_revision+1,
  'qr1OffRoadTransferEnabled',true,'areaNames',names,'physicalGeometryChanged',false,'stationPayloadsPreserved',true)::text);
 INSERT INTO public.smartfleet_demo_bootstrap VALUES(7);
END; $$;
COMMIT;
