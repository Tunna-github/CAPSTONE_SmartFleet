-- An explicitly configured transfer station supports both ends of a goods mission.
-- Dedicated WAITING/CHARGING stations do not gain transfer capabilities.
BEGIN;
ALTER TABLE public."Stations" DROP CONSTRAINT IF EXISTS "CK_Stations_Type";
ALTER TABLE public."Stations" ADD CONSTRAINT "CK_Stations_Type"
 CHECK ("StationType" IN ('PICKUP','DELIVERY','TRANSFER','WAITING','CHARGING'));
COMMIT;
