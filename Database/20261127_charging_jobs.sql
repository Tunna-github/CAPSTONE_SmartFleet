BEGIN;
ALTER TABLE public."StationCheckpoints" DROP CONSTRAINT IF EXISTS "StationCheckpoints_ArucoId_check";
ALTER TABLE public."StationCheckpoints" ADD CONSTRAINT "StationCheckpoints_ArucoId_check" CHECK ("ArucoId" BETWEEN 0 AND 249);
CREATE TABLE IF NOT EXISTS public."ChargingJobs" (
 "Id" uuid PRIMARY KEY,
 "RobotId" integer NOT NULL REFERENCES public."Robots"("RobotID"),
 "SourceMissionId" bigint NOT NULL UNIQUE REFERENCES public."Missions"("MissionID"),
 "StationId" integer REFERENCES public."Stations"("StationID"),
 "State" text NOT NULL DEFAULT 'WAITING' CHECK ("State" IN ('WAITING','RETURNING','DOCKED','READY','DEPARTING','PAUSED','FAILED','COMPLETED')),
 "Generation" integer NOT NULL,
 "MapRevision" integer NOT NULL DEFAULT 0,
 "RouteJson" text,
 "NextWaypointIndex" integer NOT NULL DEFAULT 0,
 "LastEventId" text,
 "CreatedAt" timestamp with time zone NOT NULL,
 "UpdatedAt" timestamp with time zone NOT NULL,
 "LastStoppedAt" timestamp with time zone,
 "StopAcknowledgedAt" timestamp with time zone,
 "Reason" text,
 "Revision" integer NOT NULL DEFAULT 0
);
CREATE UNIQUE INDEX IF NOT EXISTS "UX_ChargingJobs_OpenRobot" ON public."ChargingJobs"("RobotId") WHERE "State" <> 'COMPLETED';
ALTER TABLE public."ChargingJobs" ADD COLUMN IF NOT EXISTS "StopAcknowledgedAt" timestamp with time zone;
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='ChargingJobs' AND column_name='CreatedAt' AND data_type='timestamp without time zone') THEN
 ALTER TABLE public."ChargingJobs" ALTER COLUMN "CreatedAt" TYPE timestamptz USING "CreatedAt" AT TIME ZONE 'UTC',
 ALTER COLUMN "UpdatedAt" TYPE timestamptz USING "UpdatedAt" AT TIME ZONE 'UTC',
 ALTER COLUMN "LastStoppedAt" TYPE timestamptz USING "LastStoppedAt" AT TIME ZONE 'UTC';
 END IF;
END; $$;
COMMIT;
