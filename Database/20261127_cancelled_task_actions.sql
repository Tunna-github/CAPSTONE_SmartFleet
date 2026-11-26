-- Additive, repeatable migration. Never deletes task/mission history.
BEGIN;
ALTER TABLE public."TransportTasks" ADD COLUMN IF NOT EXISTS "DeletedAt" timestamptz;
ALTER TABLE public."TransportTasks" ADD COLUMN IF NOT EXISTS "DeletedBy" integer;
COMMIT;
