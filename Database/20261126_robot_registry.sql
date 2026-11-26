-- Additive FE-03 registry concurrency and identifier protection; no state reset.
BEGIN;
ALTER TABLE public."Robots" ADD COLUMN IF NOT EXISTS "RegistryRevision" integer NOT NULL DEFAULT 1;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Robot_Code_CI" ON public."Robots" (lower(btrim("RobotCode")));
CREATE UNIQUE INDEX IF NOT EXISTS "UX_Robot_Mqtt_Client_CI" ON public."Robots" (lower(btrim("MqttClientIdentifier")));
CREATE INDEX IF NOT EXISTS "IX_Tasks_Report_Created" ON public."TransportTasks" ("CreatedAt");
CREATE INDEX IF NOT EXISTS "IX_Tasks_Report_Completed" ON public."TransportTasks" ("CompletedAt");
CREATE INDEX IF NOT EXISTS "IX_Missions_Report_Created" ON public."Missions" ("CreatedAt");
COMMIT;
