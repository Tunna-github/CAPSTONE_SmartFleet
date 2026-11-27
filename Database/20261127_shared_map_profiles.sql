BEGIN;
CREATE TABLE IF NOT EXISTS public."MapDeploymentProfiles" (
 "Key" varchar(16) PRIMARY KEY CHECK ("Key" IN ('map1','map2')),
 "MapID" integer NOT NULL UNIQUE REFERENCES public."MapConfigurations"("MapID"),
 "DisplayName" varchar(120) NOT NULL,
 "SceneId" varchar(120) NOT NULL,
 "ChargingEnabled" boolean NOT NULL,
 "StationSnapshotJson" text NOT NULL CHECK ("StationSnapshotJson" IS JSON),
 "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
COMMIT;
