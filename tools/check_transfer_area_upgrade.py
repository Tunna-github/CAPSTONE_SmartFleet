"""Exercise logical bay commissioning in disposable database clones only."""
import json
import os
from pathlib import Path
import subprocess
import uuid
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(os.environ['LOCALAPPDATA']) / 'SmartFleet/charging-validation'
OUT.mkdir(parents=True, exist_ok=True)
TARGET = 'smartfleet-charging-final'
prefix = ['docker', 'compose', '-p', TARGET, '-f', 'compose.yaml', '-f', 'compose.charging.yaml', 'exec', '-T', 'database']
dump = subprocess.check_output(['docker', 'compose', '-p', 'smartfleet', 'exec', '-T', 'database', 'pg_dump', '-Fc', '-U', 'smartfleet', '-d', 'smartfleet'], cwd=ROOT)
(OUT / ('main-before-transfer-areas-' + datetime.now().strftime('%Y%m%d-%H%M%S') + '.dump')).write_bytes(dump)
names = json.loads((ROOT / 'simulation/config/transfer-area-names.json').read_text(encoding='utf8'))
template = json.loads((ROOT / 'simulation/config/dual-lane-map.json').read_text())
migration = (ROOT / 'deploy/commission-transfer-areas.sql').read_text()
migration = '\n'.join(line for line in migration.splitlines() if not line.startswith('\\set '))
def variable(key, value):
    return "\\set " + key + " '" + json.dumps(value, ensure_ascii=False).replace("'", "''") + "'\n"
migration = variable('area_names', names) + variable('area_map', template) + migration

def sql(db, query, success=True):
    p = subprocess.run(prefix + ['psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-U', 'smartfleet', '-d', db],
                       input=query, capture_output=True, encoding='utf8', cwd=ROOT)
    assert (p.returncode == 0) == success, p.stderr[-2000:]
    return p.stdout.strip()

def snapshot(db):
    tables = ['Robots', 'TransportTasks', 'TaskAssignments', 'Missions', 'MissionExecutions', 'VerificationAttempts',
              'DeliveryVerifications', 'StationBerths', 'ChargingJobs', 'Stations', 'StationCheckpoints',
              'MapConfigurations', 'SystemAuditLogs', 'smartfleet_demo_bootstrap']
    return {t: sql(db, 'SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),\'[]\'::jsonb) FROM "' + t + '" t;') for t in tables}

for case in ['valid_idempotent', 'active', 'charging_unpaused', 'charging_unacknowledged', 'custom_corridor', 'conflicting_code']:
    db = 'sf_areas_' + uuid.uuid4().hex[:12]
    sql('postgres', f'CREATE DATABASE "{db}";')
    try:
        subprocess.run(prefix + ['pg_restore', '--no-owner', '-U', 'smartfleet', '-d', db], input=dump, capture_output=True, check=True, cwd=ROOT)
        assert sql(db, 'SELECT count(*) FROM smartfleet_demo_bootstrap WHERE version=7;') == '0', 'Use a pre-upgrade fixture'
        assert sql(db, 'SELECT count(*) FROM "TaskAssignments" WHERE "AssignmentStatus"=\'ACTIVE\';') == '0'
        # Fixture state only: never substitutes for a real controller stop ACK.
        sql(db, 'UPDATE "ChargingJobs" SET "State"=\'PAUSED\',"StopAcknowledgedAt"=now() WHERE "State"<>\'COMPLETED\';')
        if case == 'active':
            sql(db, '''INSERT INTO "TransportTasks"("TaskID","TaskTrackingCode","WarehouseID","PickupStationID","DeliveryStationID","TaskStatus","CreatedBy") VALUES(900001,'AREA-SAFETY',1,1,2,'FAILED',2);
                       INSERT INTO "TaskAssignments"("AssignmentID","TaskID","RobotID","AssignmentStatus") VALUES(900001,900001,1,'ACTIVE');''')
        if case == 'charging_unpaused':
            assert sql(db, 'SELECT count(*) FROM "ChargingJobs" WHERE "State"=\'PAUSED\';') != '0'
            sql(db, 'UPDATE "ChargingJobs" SET "State"=\'READY\' WHERE "State"=\'PAUSED\';')
        if case == 'charging_unacknowledged':
            sql(db, 'UPDATE "ChargingJobs" SET "StopAcknowledgedAt"=null WHERE "State"=\'PAUSED\';')
        if case == 'custom_corridor':
            sql(db, '''UPDATE "MapConfigurations" SET "MapMetadataJson"=jsonb_set("MapMetadataJson"::jsonb,'{navigationMap,links}',("MapMetadataJson"::jsonb->'navigationMap'->'links')-(-1))::text WHERE "IsActive";''')
        if case == 'conflicting_code':
            sql(db, '''INSERT INTO "Stations"("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload") VALUES(1,'QR1','Unrelated station','WAITING',0,0,'OTHER');''')
        before = snapshot(db)
        sql(db, migration, success=case == 'valid_idempotent')
        after = snapshot(db)
        if case == 'valid_idempotent':
            for table in ['Robots', 'TransportTasks', 'TaskAssignments', 'Missions', 'MissionExecutions', 'VerificationAttempts', 'DeliveryVerifications', 'StationBerths', 'ChargingJobs']:
                assert before[table] == after[table], table
            rows = json.loads(sql(db, '''SELECT jsonb_agg(jsonb_build_object('qr',p."ArucoId",'name',s."StationName",'payload',s."QrCodePayload") ORDER BY p."ArucoId") FROM "Stations" s JOIN "StationCheckpoints" p ON p."StationId"=s."StationID" WHERE p."ArucoId" BETWEEN 0 AND 8;'''))
            assert len(rows) == 9
            for row in rows:
                assert row['name'] == names[str(row['qr'])]
                assert row['payload'] == 'QR' + str(row['qr'])
            sql(db, migration)
            assert snapshot(db) == after, 'Repeated commissioning changed data'
        else:
            assert before == after, 'Rejected upgrade must be atomic'
        print('PASS:', case, flush=True)
    finally:
        sql('postgres', f'DROP DATABASE "{db}" WITH (FORCE);')
