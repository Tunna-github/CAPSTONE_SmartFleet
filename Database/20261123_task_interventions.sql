-- Apply to the intended test/deployment database before starting the updated backend.
-- Additive migration; existing tasks and assignments are preserved.
BEGIN;
CREATE TABLE IF NOT EXISTS public."TaskInterventions" (
    "TaskId" bigint PRIMARY KEY REFERENCES public."TransportTasks"("TaskID"),
    "State" varchar(40) NOT NULL,
    "Paused" boolean NOT NULL,
    "CommandId" varchar(36),
    "Generation" integer NOT NULL DEFAULT 0,
    "CommandAcknowledged" boolean NOT NULL DEFAULT false,
    "UpdatedAt" timestamp with time zone NOT NULL
);
COMMIT;
