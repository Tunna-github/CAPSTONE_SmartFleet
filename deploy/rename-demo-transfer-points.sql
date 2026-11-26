-- Cosmetic SIM preset update. Safe during a mission: no identity, barcode,
-- mapping, task, robot, route or verification expectation is changed.
BEGIN;
SELECT pg_advisory_xact_lock(490, 20261126);
DO $$
DECLARE point record;
BEGIN
 IF to_regclass('public.smartfleet_demo_bootstrap') IS NULL THEN RETURN; END IF;
 IF NOT EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=3)
   OR EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=4) THEN RETURN; END IF;
 FOR point IN
   SELECT s."StationID",s."StationName",p."ArucoId"
   FROM public."Stations" s JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
   JOIN public."Warehouses" w ON w."WarehouseID"=p."WarehouseId"
   WHERE w."WarehouseCode"='SIM' AND w."WarehouseName"='SmartFleet simulation' AND s."StationType"='TRANSFER'
     AND ((s."StationCode"='P1' AND p."ArucoId"=2 AND s."StationName"='Pickup Station P1')
       OR (s."StationCode"='D1' AND p."ArucoId"=7 AND s."StationName"='Delivery Station D1'))
   FOR UPDATE OF s
 LOOP
   UPDATE public."Stations" SET "StationName"='Transfer point QR'||point."ArucoId",
     "Revision"="Revision"+1,"UpdatedAt"=now() WHERE "StationID"=point."StationID";
   INSERT INTO public."SystemAuditLogs" ("ActionCategory","ActionName","EntityType","EntityID","Description")
     VALUES ('WAREHOUSE','RENAME_TRANSFER_POINT','Station',point."StationID"::text,
       jsonb_build_object('source','demo-bootstrap-v4','before',point."StationName",'after','Transfer point QR'||point."ArucoId")::text);
 END LOOP;
 INSERT INTO public.smartfleet_demo_bootstrap VALUES (4);
END; $$;
COMMIT;
