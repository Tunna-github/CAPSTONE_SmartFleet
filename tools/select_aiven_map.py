"""Select Map 1/Map 2 on the single simulation host; never switches a moving fleet.

Backs up the shared database. Changing scenes is an EXPLICIT simulation reset:
requires no active missions and actual stop ACKs from docked charging robots.
Old runtime volumes and all task/mission history are retained.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from aiven_support import ROOT, STATION_SNAPSHOT, backup, private_dir, quoted, read_env, sql, update_env, map_matches_scene
from map_switch_state import ownership_sql, progress, valid_id


def api(path, token=None, data=None):
    cfg = read_env(); headers = {'Content-Type': 'application/json'}
    if token: headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request('http://127.0.0.1:' + cfg.get('WEB_PORT', '8080') + path,
        headers=headers, data=json.dumps(data).encode() if data is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=20) as r:
            body = r.read(); return json.loads(body) if body else None
    except urllib.error.HTTPError as e:
        raise RuntimeError('Application request failed (HTTP ' + str(e.code) + '): ' + e.read().decode()[:500]) from None


def profiles():
    return json.loads(sql('''SELECT json_agg(json_build_object('key',p."Key",'name',p."DisplayName",'active',p."IsSelected",'warehouseId',m."WarehouseID"))
        FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID";'''))


def select_sql(target, ready_ids, actor, request_id=None, source_warehouse=1, target_warehouse=1):
    if target not in ('map1', 'map2'): raise ValueError('Unknown map')
    source_warehouse = int(source_warehouse); target_warehouse = int(target_warehouse)
    snapshot = STATION_SNAPSHOT.replace('z."WarehouseID"=1', 'z."WarehouseID"=' + str(source_warehouse))
    ids = ','.join(str(int(i)) for i in ready_ids) or '-1'
    return '''BEGIN;
''' + ownership_sql(request_id) + '''
DO $$ BEGIN
 IF NOT pg_try_advisory_xact_lock(490,20261128) THEN RAISE EXCEPTION 'Another backend controls the shared DB. Use its web address'; END IF;
 IF EXISTS(SELECT 1 FROM "TaskAssignments" WHERE "AssignmentStatus"='ACTIVE')
 OR EXISTS(SELECT 1 FROM "Missions" WHERE "MissionStatus" NOT IN ('COMPLETED','FAILED','ABORTED'))
 THEN RAISE EXCEPTION 'Active/interrupted mission prevents map switch'; END IF;
 IF EXISTS(SELECT 1 FROM "TaskInterventions" WHERE "State" IN ('CANCEL_STOP_PENDING','RETURNING_HOME','CANCEL_REQUESTED')
 OR ("CommandId" IS NOT NULL AND NOT "CommandAcknowledged" AND "State"<>'NORMAL'))
 THEN RAISE EXCEPTION 'A task safety command still needs robot acknowledgement'; END IF;
 IF EXISTS(SELECT 1 FROM "Robots" r WHERE r."RobotType"<>'SIMULATION' OR NOT EXISTS(
 SELECT 1 FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE m."WarehouseID"=r."WarehouseID"))
 THEN RAISE EXCEPTION 'Map reset requires the configured simulation fleet'; END IF;
 IF EXISTS(SELECT 1 FROM "ChargingJobs" WHERE "State"<>'COMPLETED' AND ("State"<>'PAUSED' OR "StopAcknowledgedAt" IS NULL))
 OR EXISTS(SELECT 1 FROM "StationBerths" b WHERE NOT EXISTS(SELECT 1 FROM "ChargingJobs" j WHERE j."RobotId"=b."RobotId" AND j."StationId"=b."StationId" AND j."State"='PAUSED' AND j."StopAcknowledgedAt" IS NOT NULL))
 THEN RAISE EXCEPTION 'Docked robots need an actual stopped ACK before switching'; END IF;
END $$;
UPDATE "MapDeploymentProfiles" p SET "StationSnapshotJson"=(''' + snapshot + ''')::text,"UpdatedAt"=now()
 FROM "MapConfigurations" m WHERE m."MapID"=p."MapID" AND p."IsSelected";
-- The CLI has stopped the Webots process: its old virtual robots no longer
-- occupy physical bays. Keep charging evidence, close the reset cycle explicitly.
INSERT INTO "RobotStatusHistories" ("RobotID","PreviousOperationalStatus","NewOperationalStatus","Reason")
SELECT r."RobotID",r."OperationalStatus",'AVAILABLE','Simulation scene reset after actual charging stop acknowledgement'
FROM "Robots" r WHERE r."RobotID" IN (''' + ids + ''') AND NOT EXISTS(SELECT 1 FROM "RobotIncidents" i
WHERE i."RobotID"=r."RobotID" AND NOT i."IsResolved" AND i."Severity" IN ('ERROR','FATAL','HIGH','CRITICAL'))
AND NOT EXISTS(SELECT 1 FROM "RobotMaintenanceRecords" x WHERE x."RobotID"=r."RobotID" AND x."MaintenanceStatus" IN ('SCHEDULED','IN_PROGRESS'));
UPDATE "Robots" r SET "OperationalStatus"='AVAILABLE' WHERE r."RobotID" IN (''' + ids + ''')
 AND NOT EXISTS(SELECT 1 FROM "RobotIncidents" i WHERE i."RobotID"=r."RobotID" AND NOT i."IsResolved" AND i."Severity" IN ('ERROR','FATAL','HIGH','CRITICAL'))
 AND NOT EXISTS(SELECT 1 FROM "RobotMaintenanceRecords" x WHERE x."RobotID"=r."RobotID" AND x."MaintenanceStatus" IN ('SCHEDULED','IN_PROGRESS'));
UPDATE "ChargingJobs" SET "State"='COMPLETED',"Reason"='Explicit stopped simulation map reset; previous berth no longer exists',"UpdatedAt"=now(),"Revision"="Revision"+1 WHERE "State"='PAUSED';
DELETE FROM "StationBerths";
UPDATE "Robots" SET "ConnectionStatus"='OFFLINE',"LastHeartbeatTime"=NULL,"LastTelemetryTime"=NULL,"UpdatedAt"=now();
-- Warehouse station/mapping rows remain independent and are never rewritten.
INSERT INTO "SystemAuditLogs" ("UserID","ActionCategory","ActionName","EntityType","EntityID","Description")
SELECT ''' + str(int(actor)) + ''','SIMULATION','RELOCATE_VIRTUAL_FLEET','Robot',r."RobotID"::text,
 json_build_object('fromWarehouse',r."WarehouseID",'toWarehouse',''' + str(target_warehouse) + ''')::text
FROM "Robots" r WHERE r."RobotCode" IN ('R01','R02') AND r."WarehouseID"<>''' + str(target_warehouse) + ''';
UPDATE "Robots" SET "WarehouseID"=''' + str(target_warehouse) + ''',"RegistryRevision"="RegistryRevision"+1
 WHERE "RobotCode" IN ('R01','R02') AND "WarehouseID"<>''' + str(target_warehouse) + ''';
UPDATE "MapDeploymentProfiles" SET "IsSelected"=false WHERE "IsSelected";
UPDATE "MapDeploymentProfiles" SET "IsSelected"=true WHERE "Key"=''' + quoted(target) + ''';
INSERT INTO "SystemAuditLogs" ("UserID","ActionCategory","ActionName","EntityType","EntityID","Description")
VALUES (''' + str(int(actor)) + ''','SIMULATION','SELECT_SHARED_MAP','MapProfile',''' + quoted(target) + ''',
 'Explicit stopped scene reset. Task, mission, user and verification records retained; runtime volumes archived.');
COMMIT;'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('map', nargs='?', choices=['map1', 'map2'])
    parser.add_argument('--status', action='store_true')
    parser.add_argument('--build', action='store_true', help='Build updated code/images before starting')
    parser.add_argument('--request-id', help=argparse.SUPPRESS)
    args = parser.parse_args(); choices = profiles(); current = next(p['key'] for p in choices if p['active'])
    if args.status: print(json.dumps({'active': current, 'maps': choices}, indent=2)); return
    if not args.map: parser.error('Choose map1 or map2, or --status')
    cfg = read_env(); target = args.map
    source_warehouse = next(p['warehouseId'] for p in choices if p['active'])
    target_warehouse = next(p['warehouseId'] for p in choices if p['key'] == target)
    request_id = valid_id(args.request_id) if args.request_id else None
    if not request_id and target != current and cfg.get('MAP_SWITCH_HOST_ID'):
        if args.build: raise SystemExit('Build the current profile safely first, then select the other map through Admin or this command without --build.')
        login = api('/api/v1/auth/login', data={'usernameOrEmail':'admin','password':cfg['DEMO_ADMIN_PASSWORD']})
        token = login['accessToken']; operation = str(uuid.uuid4())
        api('/api/v1/map-profiles/select', token, {'requestId':operation,'targetKey':target,'expectedActiveKey':current,
            'confirmed':True,'reason':'Administrator selected the shared simulation map from the host CLI'})
        for _ in range(180):
            try:
                job = api('/api/v1/map-profiles', token)['selection']['operation']
                if job and job['id'] == operation:
                    if job['state'] == 'SUCCEEDED': print('Selected', target, '- backup and new world verified.'); return
                    if job['state'] in ('FAILED','FAILED_HOLD'): raise SystemExit(job['message'])
            except RuntimeError: pass # backend restarts while the durable host job continues
            time.sleep(2)
        raise SystemExit('Selection is still pending. Check web progress and host logs; do not force-clear its dispatch hold.')
    def report(state, message, saved=None):
        if request_id: progress(request_id, state, message, saved)
    sql(ownership_sql(request_id))
    requested_actor = None
    if request_id:
        job = json.loads(sql('''SELECT json_build_object('actor',"RequestedBy",'source',"SourceKey",'target',"TargetKey",'host',"HostId")
            FROM "MapSwitchRequests" WHERE "Id"=''' + quoted(request_id) + '::uuid;'))
        if job['source'] != current or job['target'] != target or job['host'] != cfg.get('MAP_SWITCH_HOST_ID'):
            raise SystemExit('Request does not match this simulation host and current map')
        requested_actor = job['actor']
        report('CHECKING', 'Checking saved map, missions and device safety.')
    saved_map = json.loads(sql('''SELECT m."MapMetadataJson"::jsonb->'navigationMap' FROM "MapDeploymentProfiles" p
        JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."Key"=''' + quoted(target) + ';'))
    manifest = json.loads((ROOT / ('simulation/config/dual-lane-manifest.json' if target == 'map2' else 'simulation/config/scene-manifest.json')).read_text())
    if not map_matches_scene(saved_map, manifest):
        raise SystemExit('Saved target map and shipped world differ. No services or map data changed; recommission the scene first.')
    if target == current and cfg.get('AIVEN_MAP_KEY') == target and not args.build:
        try:
            session = api('/api/v1/auth/login', data={'usernameOrEmail': 'operator', 'password': cfg['DEMO_OPERATOR_PASSWORD']})
            state = api('/api/v1/simulation', session['accessToken'])
            if state['mapCompatible']:
                print(target, 'is already running; no service was restarted.'); return
        except Exception: pass
    if sql('SELECT count(*) FROM "TaskAssignments" WHERE "AssignmentStatus"=\'ACTIVE\';') != '0':
        raise SystemExit('Finish/recover the active mission before rebuilding the controller')
    if target == current and sql('''SELECT count(*) FROM "ChargingJobs" WHERE "State"<>'COMPLETED'
        AND ("State"<>'PAUSED' OR "StopAcknowledgedAt" IS NULL);''') != '0':
        raise SystemExit('Before restarting this map, use Maintenance to pause charging and obtain the actual stop ACK. Resume only after fresh camera localization.')
    ready_ids = []; actor = 0
    if target != current:
        login = api('/api/v1/auth/login', data={'usernameOrEmail': 'maintenance', 'password': cfg['DEMO_MAINTENANCE_PASSWORD']})
        token = login['accessToken']; actor = requested_actor or login['user']['userId']
        if any(m['assignmentStatus'] == 'ACTIVE' for m in api('/api/v1/missions', token)):
            raise SystemExit('Finish/cancel and safely recover the active mission before switching map')
        jobs = api('/api/v1/charging', token)['jobs']
        if any(j['state'] not in ('READY', 'PAUSED') for j in jobs):
            raise SystemExit('Finish/recover charging return before changing the physical scene')
        report('BACKING_UP', 'Saving a full database backup before changing the world.')
        saved = backup('before-map-switch-' + current + '-to-' + target); print('Database backup saved:', saved)
        report('STOPPING_ROBOTS', 'Backup verified. Waiting for actual robot stop acknowledgements.', saved)
        for job in jobs:
            if job['state'] == 'READY':
                ready_ids.append(job['robotId'])
                api('/api/v1/charging/' + job['id'] + '/pause', token, {'reason': 'Explicit simulation map change; docked robot must acknowledge stop'})
        for attempt in range(60):
            pending = api('/api/v1/charging', token)['jobs']
            if all(j['state'] == 'PAUSED' and j['stopAcknowledgedAt'] for j in pending): break
            if attempt == 59: raise SystemExit('No stopped ACK; map unchanged and robots left paused')
            time.sleep(1)
        report('STOPPING_WORLD', 'Robots stopped. Stopping the previous simulation world.')
        subprocess.run(['docker', 'compose', 'stop', 'backend', 'webots'], cwd=ROOT, check=True)
        containers = subprocess.run(['docker', 'compose', 'ps', '--status', 'running', '--services'], cwd=ROOT,
            capture_output=True, text=True, check=True).stdout.split()
        if 'backend' in containers or 'webots' in containers: raise SystemExit('Processes still running; shared map unchanged')
        old_env = private_dir() / ('env-before-' + current + '-to-' + target + '-' + time.strftime('%Y%m%d-%H%M%S'))
        old_env.write_bytes((ROOT / '.env').read_bytes())
        report('SWITCHING_MAP', 'Activating the selected map and its station mappings.')
        sql(select_sql(target, ready_ids, actor, request_id, source_warehouse, target_warehouse))
        # A world reset gets fresh safety state; previous volumes remain intact.
        run = target + '-' + uuid.uuid4().hex[:10]
        cfg['AIVEN_BACKEND_VOLUME'] = 'smartfleet-aiven-backend-' + run
        cfg['AIVEN_SIMULATOR_VOLUME'] = 'smartfleet-aiven-simulator-' + run
    values = dict(AIVEN_MAP_KEY=target, AIVEN_WAREHOUSE_ID=str(target_warehouse), AIVEN_CHARGING_ENABLED='true' if target == 'map2' else 'false',
        AIVEN_MANIFEST='config/dual-lane-manifest.json' if target == 'map2' else 'config/scene-manifest.json',
        AIVEN_WORLD='worlds/smartfleet-dual-lane.wbt' if target == 'map2' else 'worlds/smartfleet.wbt',
        AIVEN_BACKEND_VOLUME=cfg['AIVEN_BACKEND_VOLUME'], AIVEN_SIMULATOR_VOLUME=cfg['AIVEN_SIMULATOR_VOLUME'])
    update_env(values)
    for name in [values['AIVEN_BACKEND_VOLUME'], values['AIVEN_SIMULATOR_VOLUME']]:
        subprocess.run(['docker', 'volume', 'create', name], check=True, stdout=subprocess.DEVNULL)
    if args.build:
        config = json.loads(subprocess.run(['docker', 'compose', 'config', '--format', 'json'], cwd=ROOT,
            capture_output=True, text=True, check=True).stdout)
        image = config['services']['webots'].get('image', config['name'] + '-webots')
        missing_world_image = subprocess.run(['docker', 'image', 'inspect', image], capture_output=True).returncode != 0
        services = ['backend', 'web'] + (['webots'] if missing_world_image else [])
        subprocess.run(['docker', 'compose', 'build', *services], cwd=ROOT, check=True)
    report('STARTING_WORLD', 'Starting the backend and matching Webots world.')
    subprocess.run(['docker', 'compose', 'up', '-d', '--no-build'], cwd=ROOT, check=True)
    for attempt in range(90):
        try:
            session = api('/api/v1/auth/login', data={'usernameOrEmail': 'operator', 'password': cfg['DEMO_OPERATOR_PASSWORD']})
            rows = api('/api/v1/map-profiles', session['accessToken'])
            state = api('/api/v1/simulation', session['accessToken'])
            if rows['activeKey'] == target and state['mapCompatible']:
                report('VERIFYING', 'Map geometry matches. Waiting for fresh robot localization.')
                print('Selected', target, '- Aiven connected; database/map/world geometry match. Open http://localhost:' + cfg.get('WEB_PORT', '8080')); return
        except Exception: pass
        time.sleep(1)
    raise SystemExit('Startup did not validate; inspect backend/Webots logs. Data and previous runtime volumes were retained.')


if __name__ == '__main__': main()
