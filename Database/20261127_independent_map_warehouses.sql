-- Run on the stopped simulation host, after a verified backup. Existing Map 2
-- tasks, users, station IDs and mission/verification snapshots stay unchanged.
BEGIN;
SELECT pg_advisory_xact_lock(490,20261128);
SELECT "ActiveRequestId" FROM "MapSwitchControl" WHERE "Id"=1 FOR UPDATE;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM "MapSwitchControl" WHERE "ActiveRequestId" IS NOT NULL AND "ActiveRequestId"
   IS DISTINCT FROM nullif(current_setting('smartfleet.map_migration_request',true),'')::uuid)
 OR EXISTS(SELECT 1 FROM "TaskAssignments" WHERE "AssignmentStatus"='ACTIVE')
 OR EXISTS(SELECT 1 FROM "ChargingJobs" WHERE "State"<>'COMPLETED') THEN
   RAISE EXCEPTION 'Finish/recover active work before separating warehouses';
 END IF;
END $$;
ALTER TABLE "MapDeploymentProfiles" ADD COLUMN IF NOT EXISTS "IsSelected" boolean NOT NULL DEFAULT false;
DO $$ BEGIN
 IF NOT EXISTS(SELECT 1 FROM "MapDeploymentProfiles" WHERE "IsSelected") THEN
  UPDATE "MapDeploymentProfiles" p SET "IsSelected"=m."IsActive" FROM "MapConfigurations" m WHERE m."MapID"=p."MapID";
 END IF;
END $$;
DO $$ DECLARE wh1 integer; wh2 integer; zone1 integer; station integer; point jsonb; BEGIN
 SELECT m."WarehouseID" INTO wh2 FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."Key"='map2';
 SELECT m."WarehouseID" INTO wh1 FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."Key"='map1';
 IF wh1=wh2 THEN
  IF EXISTS(SELECT 1 FROM "Warehouses" WHERE "WarehouseCode"='WH1') THEN RAISE EXCEPTION 'WH1 already exists; reconcile before importing'; END IF;
  INSERT INTO "Warehouses" ("WarehouseCode","WarehouseName","IsActive") VALUES ('WH1','Warehouse 1 (Map 1)',true) RETURNING "WarehouseID" INTO wh1;
  INSERT INTO "Zones" ("WarehouseID","ZoneCode","ZoneName","ZoneType","IsActive")
    VALUES (wh1,'STORAGE','Warehouse 1 transfer areas','STORAGE',true) RETURNING "ZoneID" INTO zone1;
  FOR point IN SELECT x FROM "MapDeploymentProfiles" p CROSS JOIN LATERAL jsonb_array_elements(p."StationSnapshotJson"::jsonb) x
    WHERE p."Key"='map1' AND x->>'arucoId' IS NOT NULL AND x->>'stationType' IN ('PICKUP','DELIVERY','TRANSFER') LOOP
   INSERT INTO "Stations" ("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload","IsActive")
   VALUES (zone1,'WH1-QR'||(point->>'arucoId'),point->>'stationName','TRANSFER',
     (point->>'coordX')::numeric,(point->>'coordY')::numeric,'WH1:QR'||(point->>'arucoId'),(point->>'isActive')::boolean)
   RETURNING "StationID" INTO station;
   INSERT INTO "StationCheckpoints" ("StationId","WarehouseId","ArucoId") VALUES (station,wh1,(point->>'arucoId')::integer);
  END LOOP;
  UPDATE "MapConfigurations" SET "WarehouseID"=wh1,"IsActive"=true WHERE "MapID"=(SELECT "MapID" FROM "MapDeploymentProfiles" WHERE "Key"='map1');
  UPDATE "MapDeploymentProfiles" SET "StationSnapshotJson"=(SELECT jsonb_agg(jsonb_build_object(
    'stationId',s."StationID",'stationName',s."StationName",'stationType',s."StationType",'isActive',s."IsActive",
    'coordX',s."CoordX",'coordY',s."CoordY",'qrCodePayload',s."QrCodePayload",'arucoId',c."ArucoId"))::text
    FROM "Stations" s JOIN "Zones" z ON z."ZoneID"=s."ZoneID" JOIN "StationCheckpoints" c ON c."StationId"=s."StationID" WHERE z."WarehouseID"=wh1)
    WHERE "Key"='map1';
  INSERT INTO "SystemAuditLogs" ("ActionCategory","ActionName","EntityType","EntityID","Description")
    VALUES ('SIMULATION','SEPARATE_MAP_WAREHOUSES','Warehouse',wh1::text,'Map 1 received independent stations and warehouse. Existing Map 2 task/user/mission IDs retained.');
 END IF;
 UPDATE "Warehouses" SET "WarehouseName"='Current Warehouse (Map 2)',"UpdatedAt"=now(),"Revision"="Revision"+1 WHERE "WarehouseID"=wh2 AND "WarehouseName"<>'Current Warehouse (Map 2)';
 UPDATE "MapDeploymentProfiles" SET "DisplayName"=CASE "Key" WHEN 'map1' THEN 'Warehouse 1 · Map 1' ELSE 'Current Warehouse · Map 2' END;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_MapProfile_OneSelected" ON "MapDeploymentProfiles" ("IsSelected") WHERE "IsSelected";
COMMIT;
