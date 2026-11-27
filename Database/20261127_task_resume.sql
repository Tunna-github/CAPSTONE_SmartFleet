BEGIN;
ALTER TABLE "TaskInterventions" ADD COLUMN IF NOT EXISTS "PauseContextJson" text;
ALTER TABLE "TaskInterventions" ADD COLUMN IF NOT EXISTS "ResumeFailureReason" varchar(400);
ALTER TABLE "TaskInterventions" ADD COLUMN IF NOT EXISTS "ResumedAt" timestamptz;
COMMIT;
