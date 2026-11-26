-- Canonical SIM station barcodes. Existing MissionExecutions and verification
-- history are deliberately immutable: running/held missions keep their old codes.
BEGIN;
SELECT pg_advisory_xact_lock(490, 20261126);
DO $$
DECLARE point record;
BEGIN
 IF to_regclass('public.smartfleet_demo_bootstrap') IS NULL THEN RETURN; END IF;
 IF NOT EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=4)
   OR EXISTS (SELECT 1 FROM public.smartfleet_demo_bootstrap WHERE version=5) THEN RETURN; END IF;
 LOCK TABLE public."Warehouses",public."Stations",public."StationCheckpoints" IN SHARE ROW EXCLUSIVE MODE;
 IF (SELECT count(*) FROM public."Stations" s JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
   JOIN public."Warehouses" w ON w."WarehouseID"=p."WarehouseId"
   WHERE w."WarehouseCode"='SIM' AND w."WarehouseName"='SmartFleet simulation' AND s."StationType"='TRANSFER'
     AND ((s."StationCode"='P1' AND p."ArucoId"=2 AND s."QrCodePayload" IN ('P1','QR2'))
       OR (s."StationCode"='D1' AND p."ArucoId"=7 AND s."QrCodePayload" IN ('D1','QR7')))) <> 2 THEN
   RAISE EXCEPTION 'Cannot unify SIM verification codes: expected P1/QR2 and D1/QR7 mappings/payloads were customized.';
 END IF;
 IF EXISTS (SELECT 1 FROM public."Stations" s WHERE s."QrCodePayload" IN ('QR2','QR7') AND NOT EXISTS (
   SELECT 1 FROM public."StationCheckpoints" p JOIN public."Warehouses" w ON w."WarehouseID"=p."WarehouseId"
   WHERE p."StationId"=s."StationID" AND w."WarehouseCode"='SIM' AND s."StationType"='TRANSFER'
     AND ((s."StationCode"='P1' AND p."ArucoId"=2 AND s."QrCodePayload"='QR2')
       OR (s."StationCode"='D1' AND p."ArucoId"=7 AND s."QrCodePayload"='QR7')))) THEN
   RAISE EXCEPTION 'Cannot unify SIM verification codes: QR2 or QR7 payload is owned by another station.';
 END IF;
 FOR point IN
   SELECT s."StationID",s."QrCodePayload",p."ArucoId" FROM public."Stations" s
   JOIN public."StationCheckpoints" p ON p."StationId"=s."StationID"
   JOIN public."Warehouses" w ON w."WarehouseID"=p."WarehouseId"
   WHERE w."WarehouseCode"='SIM' AND s."StationType"='TRANSFER' AND
     ((s."StationCode"='P1' AND p."ArucoId"=2 AND s."QrCodePayload"='P1')
       OR (s."StationCode"='D1' AND p."ArucoId"=7 AND s."QrCodePayload"='D1')) FOR UPDATE OF s
 LOOP
   UPDATE public."Stations" SET "QrCodePayload"='QR'||point."ArucoId","Revision"="Revision"+1,"UpdatedAt"=now()
     WHERE "StationID"=point."StationID";
   INSERT INTO public."SystemAuditLogs" ("ActionCategory","ActionName","EntityType","EntityID","Description")
     VALUES ('WAREHOUSE','UPDATE_VERIFICATION_CODE','Station',point."StationID"::text,
       jsonb_build_object('source','demo-bootstrap-v5','before',point."QrCodePayload",'after','QR'||point."ArucoId",'policy','EXISTING_MISSIONS_KEEP_FROZEN_CODES')::text);
 END LOOP;
 INSERT INTO public.smartfleet_demo_bootstrap VALUES (5);
END; $$;
COMMIT;
