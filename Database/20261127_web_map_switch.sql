BEGIN;
CREATE TABLE IF NOT EXISTS "MapSwitchRequests" (
 "Id" uuid PRIMARY KEY,
 "HostId" uuid NOT NULL,
 "SourceKey" varchar(16) NOT NULL CHECK ("SourceKey" IN ('map1','map2')),
 "TargetKey" varchar(16) NOT NULL CHECK ("TargetKey" IN ('map1','map2')),
 "RequestedBy" integer NOT NULL REFERENCES "Users"("UserID"),
 "Reason" varchar(500) NOT NULL,
 "State" varchar(32) NOT NULL DEFAULT 'REQUESTED',
 "Message" varchar(500) NOT NULL DEFAULT 'Waiting for the simulation host.',
 "BackupName" text, "BackupSha256" varchar(64),
 "RequestedAt" timestamptz NOT NULL DEFAULT now(),
 "UpdatedAt" timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS "MapSwitchControl" (
 "Id" integer PRIMARY KEY CHECK ("Id"=1),
 "ActiveRequestId" uuid REFERENCES "MapSwitchRequests"("Id")
);
INSERT INTO "MapSwitchControl" ("Id") VALUES (1) ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS "MapSwitchWorkers" (
 "HostId" uuid PRIMARY KEY, "LastSeenAt" timestamptz NOT NULL DEFAULT now()
);
COMMIT;
