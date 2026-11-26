"""Exercise real PostgreSQL demo commissioning in disposable database clones only.

First initialize a separate Compose project named smartfleet-station-<suffix>.
This tool never targets the normal smartfleet project or operational databases.
"""
import argparse
import json
from pathlib import Path
import subprocess
import uuid

parser = argparse.ArgumentParser()
parser.add_argument('--project', required=True)
parser.add_argument('--qr-transfer', action='store_true')
parser.add_argument('--rename-labels', action='store_true')
args = parser.parse_args()
assert args.project.startswith('smartfleet-station-'), 'Use a dedicated smartfleet-station-* test project'
root = Path(__file__).resolve().parents[1]
command = ['docker', 'compose', '-p', args.project, 'exec', '-T', 'database', 'psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-U', 'smartfleet']
preset = (root / 'simulation/config/map.json').read_text()
migration = (root / 'deploy/commission-demo-stations.sql').read_text()
if args.qr_transfer:
    migration = (root / 'deploy/commission-demo-qr-stations.sql').read_text().replace('\\ir commission-demo-stations.sql', migration)


def sql(database, statement, migrate=False):
    if migrate:
        # stdin avoids Windows' command-line length limit for the full map document.
        statement = '\\set demo_map_json supplied\n' + statement.replace(":'demo_map_json'", "'" + preset.replace("'", "''") + "'")
    result = subprocess.run(command + ['-d', database], input=statement,
                            capture_output=True, text=True, encoding='utf-8', cwd=root)
    assert result.returncode == 0, result.stderr[-1800:]
    return result.stdout.strip()


def state(database):
    return sql(database, '''SELECT jsonb_build_object(
      'mappings',(SELECT jsonb_agg(to_jsonb(p) ORDER BY "StationId") FROM "StationCheckpoints" p),
      'stations',(SELECT jsonb_agg(to_jsonb(s) ORDER BY "StationID") FROM "Stations" s),
      'maps',(SELECT jsonb_agg(to_jsonb(m) ORDER BY "MapID") FROM "MapConfigurations" m),
      'tasks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY "TaskID") FROM "TransportTasks" t),
      'assignments',(SELECT jsonb_agg(to_jsonb(a) ORDER BY "AssignmentID") FROM "TaskAssignments" a),
      'robots',(SELECT jsonb_agg(to_jsonb(r) ORDER BY "RobotID") FROM "Robots" r),
      'audit',(SELECT jsonb_agg(to_jsonb(l) ORDER BY "AuditID") FROM "SystemAuditLogs" l));''')


def mapping(database):
    return json.loads(sql(database, '''SELECT jsonb_object_agg(s."StationCode",p."ArucoId")
        FROM "Stations" s JOIN "StationCheckpoints" p ON p."StationId"=s."StationID";''') or '{}')


if args.rename_labels:
    labels = (root / 'deploy/rename-demo-transfer-points.sql').read_text()
    prepare = '''DELETE FROM smartfleet_demo_bootstrap WHERE version=4;
        UPDATE "Stations" SET "StationName"=CASE "StationCode" WHEN 'P1' THEN 'Pickup Station P1' ELSE 'Delivery Station D1' END WHERE "StationCode" IN ('P1','D1');'''
    for custom in [False, True]:
        database = 'sf_station_' + uuid.uuid4().hex[:12]
        sql('postgres', f'CREATE DATABASE "{database}" TEMPLATE smartfleet;')
        try:
            sql(database, prepare)
            if custom:
                sql(database, '''UPDATE "Stations" SET "StationName"='Custom packing point' WHERE "StationCode"='P1';''')
            # Exercise metadata-only renaming while an assignment exists.
            sql(database, '''INSERT INTO "TransportTasks" ("TaskID","TaskTrackingCode","WarehouseID","PickupStationID","DeliveryStationID","TaskStatus","CreatedBy") VALUES (99,'LABEL-HOLD',1,1,2,'EXECUTING',2);
                INSERT INTO "TaskAssignments" ("TaskID","RobotID","AssignmentStatus") VALUES (99,1,'ACTIVE');''')
            before = json.loads(state(database))
            sql(database, labels)
            after = json.loads(state(database))
            for key in ['maps', 'tasks', 'assignments', 'robots', 'mappings']:
                assert before[key] == after[key], key
            originals = {s['StationID']: s for s in before['stations']}
            for station in after['stations']:
                previous = originals[station['StationID']]
                for key in set(previous) - {'StationName', 'Revision', 'UpdatedAt'}:
                    assert station[key] == previous[key], key
                if station['StationCode'] == 'P1':
                    assert station['StationName'] == ('Custom packing point' if custom else 'Transfer point QR2')
                if station['StationCode'] == 'D1':
                    assert station['StationName'] == 'Transfer point QR7'
            snapshot = state(database)
            sql(database, labels); assert state(database) == snapshot
            print('PASS: neutral labels, active-task/barcode preservation, idempotency, custom=' + str(custom), flush=True)
        finally:
            sql('postgres', f'DROP DATABASE "{database}";')
    raise SystemExit(0)


reset_legacy = """DELETE FROM "TaskStatusHistories"; DELETE FROM "TransportTasks"; DELETE FROM "StationCheckpoints"; DELETE FROM "Stations" WHERE "StationCode" NOT IN ('P1','D1');
UPDATE "Stations" SET "StationType"=CASE "StationCode" WHEN 'P1' THEN 'PICKUP' ELSE 'DELIVERY' END,"QrCodePayload"=CASE "StationCode" WHEN 'P1' THEN 'P1' ELSE 'D1' END,"Revision"=1;
DELETE FROM smartfleet_demo_bootstrap WHERE version IN (2,3,4,5); DELETE FROM "SystemAuditLogs";"""
cases = {
    'fresh_idempotent': '',
    'legacy_code_identity': reset_legacy + '''UPDATE "Stations" SET "StationID"=CASE "StationCode" WHEN 'P1' THEN 101 ELSE 202 END;''',
    'custom_mapping': reset_legacy + '''INSERT INTO "StationCheckpoints" VALUES (1,1,3);''',
    'changed_map': reset_legacy + '''UPDATE "MapConfigurations" SET "MapMetadataJson"=jsonb_set("MapMetadataJson"::jsonb,'{navigationMap,cells,1,4,arucoId}','9')::text;''',
    'changed_corridors': reset_legacy + '''UPDATE "MapConfigurations" SET "MapMetadataJson"=jsonb_set("MapMetadataJson"::jsonb,'{navigationMap,links}','[]')::text;''',
    'marker_conflict': reset_legacy + '''INSERT INTO "Stations" ("ZoneID","StationCode","StationName","StationType","CoordX","CoordY") VALUES (1,'OTHER','Other waiting berth','WAITING',0,0); INSERT INTO "StationCheckpoints" SELECT "StationID",1,2 FROM "Stations" WHERE "StationCode"='OTHER';''',
    'customized_station': reset_legacy + '''UPDATE "Stations" SET "Revision"=2 WHERE "StationCode"='P1';''',
    'active_failed_hold': reset_legacy + '''INSERT INTO "TransportTasks" ("TaskID","TaskTrackingCode","WarehouseID","PickupStationID","DeliveryStationID","TaskStatus","CreatedBy") VALUES (1,'TEST-HOLD',1,1,2,'FAILED',2); INSERT INTO "TaskAssignments" ("TaskID","RobotID","AssignmentStatus") VALUES (1,1,'ACTIVE');''',
    'non_demo': reset_legacy + 'DROP TABLE smartfleet_demo_bootstrap;',
}
if args.qr_transfer:
    cases.update({
        'dedicated_waiting': '''DELETE FROM smartfleet_demo_bootstrap WHERE version=3; UPDATE "Stations" SET "StationType"='WAITING',"QrCodePayload"=NULL WHERE "StationCode"='QR0';''',
        'missing_transfer_verification': '''DELETE FROM smartfleet_demo_bootstrap WHERE version=3; UPDATE "Stations" SET "QrCodePayload"=NULL WHERE "StationCode"='QR0';''',
    })
for name, preparation in cases.items():
    database = 'sf_station_' + uuid.uuid4().hex[:12]
    sql('postgres', f'CREATE DATABASE "{database}" TEMPLATE smartfleet;')
    try:
        sql(database, preparation)
        before = state(database)
        sql(database, migration, migrate=True)
        after = state(database)
        if name == 'legacy_code_identity':
            assert mapping(database)['P1'] == 2 and mapping(database)['D1'] == 7
            if args.qr_transfer:
                assert sorted(mapping(database).values()) == [0,2,3,4,5,6,7,8]
            a, b = json.loads(after), json.loads(before)
            for key in ['maps', 'tasks', 'assignments', 'robots']:
                assert a[key] == b[key], (name, key)
            assert len(a['audit']) == (10 if args.qr_transfer else 2)
            assert {101,202}.issubset({s['StationID'] for s in a['stations']})
            sql(database, migration, migrate=True)
            assert state(database) == after, 'Rerun must not duplicate mappings, audit entries or revisions'
            # After completing the one-time upgrade, deliberately changed mappings stay changed.
            sql(database, 'DELETE FROM "StationCheckpoints";')
            intentional = state(database)
            sql(database, migration, migrate=True)
            assert state(database) == intentional
        else:
            assert before == after, name + ': migration changed protected data'
        print('PASS:', name, flush=True)
    finally:
        sql('postgres', f'DROP DATABASE "{database}";')
print(f'PASS: {len(cases)} PostgreSQL commissioning/upgrade cases; disposable database clones removed.')
