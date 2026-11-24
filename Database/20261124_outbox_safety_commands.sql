-- Existing assignments need multiple durable Pause/Cancel/Home commands.
-- The old unique AssignmentId index incorrectly rejects all such commands.
-- Preserve uniqueness for the original EXECUTE_TASK only; preserve every row.
BEGIN;
CREATE UNIQUE INDEX IF NOT EXISTS "UX_RobotCommandOutbox_OneMissionPerAssignment"
    ON public."RobotCommandOutbox" ("AssignmentId")
    WHERE ("Payload"::jsonb ->> 'command') = 'EXECUTE_TASK';
DROP INDEX IF EXISTS public."IX_RobotCommandOutbox_AssignmentId";
CREATE INDEX "IX_RobotCommandOutbox_AssignmentId"
    ON public."RobotCommandOutbox" ("AssignmentId");
COMMIT;
