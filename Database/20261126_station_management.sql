-- Additive FE-02 migration. No guessed mapping of historical StationIDs to QR IDs.
BEGIN;
ALTER TABLE public."Warehouses" ADD COLUMN IF NOT EXISTS "Revision" integer NOT NULL DEFAULT 1;
ALTER TABLE public."Zones" ADD COLUMN IF NOT EXISTS "Revision" integer NOT NULL DEFAULT 1;
ALTER TABLE public."Stations" ADD COLUMN IF NOT EXISTS "Revision" integer NOT NULL DEFAULT 1;
ALTER TABLE public."Stations" ADD COLUMN IF NOT EXISTS "Capacity" integer NOT NULL DEFAULT 1 CHECK ("Capacity" = 1);
CREATE TABLE IF NOT EXISTS public."StationCheckpoints" (
 "StationId" integer PRIMARY KEY REFERENCES public."Stations"("StationID"),
 "WarehouseId" integer NOT NULL REFERENCES public."Warehouses"("WarehouseID"),
 "ArucoId" integer NOT NULL CHECK ("ArucoId" BETWEEN 0 AND 49),
 UNIQUE ("WarehouseId", "ArucoId")
);
CREATE TABLE IF NOT EXISTS public."StationBerths" (
 "StationId" integer PRIMARY KEY REFERENCES public."Stations"("StationID"),
 "RobotId" integer NOT NULL UNIQUE REFERENCES public."Robots"("RobotID"),
 "ReservedAt" timestamptz NOT NULL DEFAULT now()
);
-- Case-insensitive catalog identifiers; fail visibly on conflicting legacy data.
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Warehouse_Code_CI" ON public."Warehouses" (lower(btrim("WarehouseCode")));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Zone_Code_CI" ON public."Zones" ("WarehouseID", lower(btrim("ZoneCode")));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Zone_Name_CI" ON public."Zones" ("WarehouseID", lower(btrim("ZoneName")));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Station_Code_CI_Global" ON public."Stations" (lower(btrim("StationCode")));
-- Cross-table consistency is also checked by the service in serializable transactions.
CREATE OR REPLACE FUNCTION public.smartfleet_station_mapping_warehouse() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM public."Stations" s JOIN public."Zones" z ON z."ZoneID"=s."ZoneID"
                WHERE s."StationID"=NEW."StationId" AND z."WarehouseID"=NEW."WarehouseId") THEN
  RAISE EXCEPTION 'Station checkpoint warehouse does not match station zone' USING ERRCODE='23514';
 END IF;
 RETURN NEW;
END; $$;
DROP TRIGGER IF EXISTS station_mapping_warehouse ON public."StationCheckpoints";
CREATE TRIGGER station_mapping_warehouse BEFORE INSERT OR UPDATE ON public."StationCheckpoints"
 FOR EACH ROW EXECUTE FUNCTION public.smartfleet_station_mapping_warehouse();
COMMIT;
