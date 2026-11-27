"""Durable, credential-free progress for Admin map selection."""
import hashlib
import uuid
from aiven_support import quoted, sql, pg_command

TERMINAL = ('SUCCEEDED', 'FAILED', 'FAILED_HOLD')
STOP_STAGES = ('STOPPING_WORLD', 'SWITCHING_MAP', 'STARTING_WORLD', 'VERIFYING')


def valid_id(value):
    return str(uuid.UUID(str(value)))


def ownership_sql(request_id=None):
    expected = 'NULL::uuid' if request_id is None else quoted(valid_id(request_id)) + '::uuid'
    return '''DO $$ BEGIN
      IF (SELECT "ActiveRequestId" FROM "MapSwitchControl" WHERE "Id"=1 FOR UPDATE)
          IS DISTINCT FROM ''' + expected + ''' THEN
        RAISE EXCEPTION 'A different Admin map transition owns the dispatch gate';
      END IF;
    END $$;'''


def progress(request_id, state, message, backup=None):
    request_id = valid_id(request_id)
    extra = ''
    if backup:
        # Validate the archive before any world is stopped. Paths/credentials
        # stay host-local; only a basename and checksum are exposed to the web.
        with backup.open('rb') as source:
            pg_command('pg_restore', ['--list'], source.read())
        digest = hashlib.sha256(backup.read_bytes()).hexdigest()
        extra = ',"BackupName"=' + quoted(backup.name) + ',"BackupSha256"=' + quoted(digest)
    sql('UPDATE "MapSwitchRequests" SET "State"=' + quoted(state) + ',"Message"=' + quoted(message[:500]) +
        ',"UpdatedAt"=now()' + extra + ' WHERE "Id"=' + quoted(request_id) + '::uuid;')


def finish(request_id, state, message):
    if state not in TERMINAL: raise ValueError('Invalid terminal state')
    request_id = valid_id(request_id)
    sql('BEGIN;' + ownership_sql(request_id) + '''
        UPDATE "MapSwitchRequests" SET "State"=''' + quoted(state) + ',"Message"=' + quoted(message[:500]) + ''',"UpdatedAt"=now()
        WHERE "Id"=''' + quoted(request_id) + '''::uuid;
        ''' + ('''UPDATE "MapSwitchControl" SET "ActiveRequestId"=NULL WHERE "Id"=1;''' if state != 'FAILED_HOLD' else '') + '''
        INSERT INTO "SystemAuditLogs" ("UserID","ActionCategory","ActionName","EntityType","EntityID","Description")
        SELECT "RequestedBy",'SIMULATION','MAP_SWITCH_RESULT','MapProfile',"TargetKey",''' + quoted(state + ': ' + message[:450]) + '''
        FROM "MapSwitchRequests" WHERE "Id"=''' + quoted(request_id) + '::uuid;COMMIT;')
