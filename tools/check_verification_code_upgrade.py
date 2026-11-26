"""Real PostgreSQL code-cutover checks in disposable database clones only."""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--project', required=True)
args = parser.parse_args()
assert args.project.startswith('smartfleet-station-'), 'Use an isolated station test stack with backend stopped'
root = Path(__file__).resolve().parents[1]
migration = (root / 'deploy/unify-demo-verification-codes.sql').read_text()
command = ['docker', 'compose', '-p', args.project, 'exec', '-T', 'database', 'psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-U', 'smartfleet']


def sql(database, script, success=True):
    response = subprocess.run(command + ['-d', database], input=script, capture_output=True, encoding='utf-8', cwd=root)
    assert (response.returncode == 0) == success, response.stderr[-1500:]
    return response.stdout.strip()


def snapshot(database):
    return json.loads(sql(database, '''SELECT jsonb_build_object(
      'stations',(SELECT jsonb_agg(to_jsonb(s) ORDER BY "StationID") FROM "Stations" s),
      'mappings',(SELECT jsonb_agg(to_jsonb(p) ORDER BY "StationId") FROM "StationCheckpoints" p),
      'maps',(SELECT jsonb_agg(to_jsonb(m) ORDER BY "MapID") FROM "MapConfigurations" m),
      'tasks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "TaskID") FROM "TransportTasks" t),
      'assignments',(SELECT jsonb_agg(to_jsonb(a) ORDER BY "AssignmentID") FROM "TaskAssignments" a),
      'robots',(SELECT jsonb_agg(to_jsonb(r) ORDER BY "RobotID") FROM "Robots" r),
      'executions',(SELECT jsonb_agg(to_jsonb(e) ORDER BY "MissionId") FROM "MissionExecutions" e),
      'attempts',(SELECT jsonb_agg(to_jsonb(v) ORDER BY "Id") FROM "VerificationAttempts" v),
      'audit',(SELECT jsonb_agg(to_jsonb(l) ORDER BY "AuditID") FROM "SystemAuditLogs" l),
      'versions',(SELECT jsonb_agg(version ORDER BY version) FROM smartfleet_demo_bootstrap));'''))


for case in ['active', 'failed_hold', 'idempotent', 'conflict', 'custom_payload']:
    database = 'sf_codes_' + uuid.uuid4().hex[:12]
    sql('postgres', f'CREATE DATABASE "{database}" TEMPLATE smartfleet;')
    try:
        sql(database, '''DELETE FROM smartfleet_demo_bootstrap WHERE version=5;
          INSERT INTO smartfleet_demo_bootstrap VALUES (4) ON CONFLICT DO NOTHING;
          UPDATE "Stations" SET "QrCodePayload"=CASE "StationCode" WHEN 'P1' THEN 'P1' ELSE 'D1' END WHERE "StationCode" IN ('P1','D1');''')
        if case in ['active', 'failed_hold']:
            status = 'AT_PICKUP' if case == 'active' else 'FAILED'
            task_status = 'EXECUTING' if case == 'active' else 'FAILED'
            sql(database, f'''INSERT INTO "TransportTasks" ("TaskID","TaskTrackingCode","WarehouseID","PickupStationID","DeliveryStationID","TaskStatus","CreatedBy") VALUES (99,'CODE-UPGRADE',1,1,2,'{task_status}',2);
              INSERT INTO "TaskAssignments" ("AssignmentID","TaskID","RobotID","AssignmentStatus") VALUES (99,99,1,'ACTIVE');
              INSERT INTO "Missions" ("MissionID","AssignmentID","MissionCode","MissionStatus") VALUES (99,99,'CODE-UPGRADE','{status}');
              INSERT INTO "MissionExecutions" ("MissionId","RoutePayloadJson","MapRevision","ExpectedPickupQr","ExpectedDeliveryQr","UpdatedAt") VALUES (99,'{{"fixture":"frozen route"}}',5,'P1','D1',now());
              INSERT INTO "VerificationAttempts" ("MissionId","RequestId","RequestHash","Stage","Type","ScannedQr","ExpectedQr","Accepted","Result","UserId","CreatedAt") VALUES (99,'fixture','fixture','PICKUP','QRCODE','WRONG','P1',false,'QR_OR_PACKAGE_MISMATCH',2,now());''')
        if case == 'conflict':
            sql(database, '''INSERT INTO "Stations" ("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload") VALUES (1,'CODE-CONFLICT','Existing barcode','WAITING',0,0,'QR2');''')
        if case == 'custom_payload':
            sql(database, '''UPDATE "Stations" SET "QrCodePayload"='CUSTOM:P1' WHERE "StationCode"='P1';''')
        before = snapshot(database)
        sql(database, migration, success=case not in ['conflict', 'custom_payload'])
        after = snapshot(database)
        if case in ['conflict', 'custom_payload']:
            assert before == after, 'Rejected cutover must roll back both station updates and the version marker'
        else:
            for key in ['mappings', 'maps', 'tasks', 'assignments', 'robots', 'executions', 'attempts']:
                assert before[key] == after[key], key
            codes = {s['StationCode']: s['QrCodePayload'] for s in after['stations']}
            assert codes['P1'] == 'QR2' and codes['D1'] == 'QR7'
            originals = {s['StationID']: s for s in before['stations']}
            for station in after['stations']:
                for key in set(station) - {'QrCodePayload', 'Revision', 'UpdatedAt'}:
                    assert station[key] == originals[station['StationID']][key], key
            sql(database, migration)
            assert snapshot(database) == after, 'Repeat must not duplicate audit entries or revisions'
        print('PASS:', case, flush=True)
    finally:
        sql('postgres', f'DROP DATABASE "{database}";')
print('PASS: barcode cutover preserves running/failed missions and historical scans; conflicts are atomic.')
