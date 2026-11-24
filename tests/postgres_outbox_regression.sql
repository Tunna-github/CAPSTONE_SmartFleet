-- Run after 20261124_outbox_safety_commands.sql. Copies the ACTUAL indexes into
-- a temporary table; no live task, robot, outbox row or sequence is changed.
BEGIN;
CREATE TEMP TABLE audit_outbox (LIKE public."RobotCommandOutbox" INCLUDING ALL) ON COMMIT DROP;
INSERT INTO audit_outbox ("Id", "AssignmentId", "Topic", "Payload", "CreatedAt") VALUES
    (1, 1, 'test', '{"command":"EXECUTE_TASK"}', NOW()),
    (2, 1, 'test', '{"command":"PAUSE_TASK","commandId":"a"}', NOW()),
    (3, 1, 'test', '{"command":"CANCEL_STOP","commandId":"b"}', NOW());
DO $$ BEGIN
    BEGIN
        INSERT INTO audit_outbox ("Id", "AssignmentId", "Topic", "Payload", "CreatedAt")
        VALUES (4, 1, 'test', '{"command":"EXECUTE_TASK"}', NOW());
        RAISE EXCEPTION 'Duplicate mission command incorrectly accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    IF (SELECT COUNT(*) FROM audit_outbox) <> 3 THEN
        RAISE EXCEPTION 'Expected one mission and two safety controls';
    END IF;
END $$;
ROLLBACK;
