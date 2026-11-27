"""Host-only worker for authenticated Admin map requests. No HTTP listener or Docker socket in the backend.

Run on the one Docker simulation host. Requests, progress and the dispatch hold
survive backend restarts. A crash during a transition leaves a recovery hold.
"""
import argparse
import contextlib
import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
import uuid
import traceback
import hashlib
from aiven_support import ROOT, private_dir, quoted, read_env, sql, update_env, backup, pg_command
from map_switch_state import finish, TERMINAL, STOP_STAGES
from select_aiven_map import api


@contextlib.contextmanager
def single_worker():
    path = private_dir() / 'map-switch-worker.lock'
    with path.open('a+b') as handle:
        handle.seek(0); handle.write(b'0'); handle.flush(); handle.seek(0)
        if os.name == 'nt':
            import msvcrt
            msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def heartbeat(host):
    sql('INSERT INTO "MapSwitchWorkers" ("HostId") VALUES (' + quoted(host) + '''::uuid)
        ON CONFLICT ("HostId") DO UPDATE SET "LastSeenAt"=now();''')


def claim(host):
    result = sql('''WITH claimed AS (UPDATE "MapSwitchRequests" r SET "State"='CHECKING',"Message"='Validating the selected world.',"UpdatedAt"=now()
        WHERE r."HostId"=''' + quoted(host) + '''::uuid AND r."State"='REQUESTED'
        AND r."Id"=(SELECT "ActiveRequestId" FROM "MapSwitchControl" WHERE "Id"=1)
        RETURNING r."Id",r."TargetKey") SELECT json_build_object('id',"Id",'target',"TargetKey") FROM claimed;''')
    return json.loads(result) if result else None


def verify_world(target, timeout=150):
    cfg = read_env(); deadline = time.monotonic() + timeout
    expected_scene = 'smartfleet-dual-lane-r2025a-v2' if target == 'map2' else 'smartfleet-demo-r2025a-v1'
    while time.monotonic() < deadline:
        try:
            token = api('/api/v1/auth/login', data={'usernameOrEmail':'operator','password':cfg['DEMO_OPERATOR_PASSWORD']})['accessToken']
            scene = api('/api/v1/simulation', token)
            positions = api('/api/v1/demo-routing/robots/positions', token)
            poses = api('/api/v1/simulation/poses', token)['poses']
            if (scene['mapCompatible'] and scene['sceneId'] == expected_scene and scene.get('streamReady') and
                all(code in positions for code in ('R01','R02')) and {p['robotCode'] for p in poses} >= {'R01','R02'}):
                return
        except Exception: pass
        time.sleep(2)
    raise RuntimeError('New world did not report fresh camera localization for both demo robots')


def run_job(job):
    log = private_dir() / ('map-switch-' + job['id'] + '.log')
    with log.open('w', encoding='utf-8') as output:
        result = subprocess.run([sys.executable, '-u', str(ROOT/'tools/select_aiven_map.py'), job['target'],
            '--request-id', job['id']], cwd=ROOT, stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode == 0:
        try:
            verify_world(job['target'])
            finish(job['id'], 'SUCCEEDED', 'Map and Webots world changed. Both robots localized; dispatch is enabled.')
            return
        except Exception: pass
    state = sql('SELECT "State" FROM "MapSwitchRequests" WHERE "Id"=' + quoted(job['id']) + '::uuid;')
    # Before STOPPING_WORLD the map is unchanged; paused devices remain held.
    held = state in STOP_STAGES
    finish(job['id'], 'FAILED_HOLD' if held else 'FAILED',
        'World change could not be verified. Dispatch remains locked; host Maintenance recovery is required.' if held else
        'Map was not changed. Check the host log and any robot stop/charging hold before retrying.')


def main():
    if os.name == 'nt':
        docker_bin = Path(os.environ['LOCALAPPDATA']) / 'Programs/DockerDesktop/resources/bin'
        if docker_bin.is_dir(): os.environ['PATH'] = str(docker_bin) + os.pathsep + os.environ.get('PATH', '')
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument('--configure', action='store_true')
    parser.add_argument('--verify-recovery', type=uuid.UUID, help='Verify the already-selected world and retained backup before releasing a failed switch hold')
    args = parser.parse_args(); cfg = read_env()
    if args.verify_recovery:
        rid=str(args.verify_recovery)
        row=json.loads(sql('''SELECT json_build_object('target',r."TargetKey",'host',r."HostId",'state',r."State",'backup',r."BackupName",'hash',r."BackupSha256",'warehouse',m."WarehouseID")
            FROM "MapSwitchRequests" r JOIN "MapSwitchControl" c ON c."ActiveRequestId"=r."Id"
            JOIN "MapDeploymentProfiles" p ON p."Key"=r."TargetKey" AND p."IsSelected"
            JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE r."Id"='''+quoted(rid)+'::uuid;'))
        assert row['state']=='FAILED_HOLD' and row['host']==cfg['MAP_SWITCH_HOST_ID'] and row['target']==cfg['AIVEN_MAP_KEY']
        assert str(row['warehouse'])==cfg['AIVEN_WAREHOUSE_ID']
        archive=(private_dir()/row['backup']).resolve();assert archive.parent==private_dir().resolve()
        assert hashlib.sha256(archive.read_bytes()).hexdigest()==row['hash']
        pg_command('pg_restore',['--list'],archive.read_bytes())
        assert sql('SELECT count(*) FROM "TaskAssignments" WHERE "AssignmentStatus"=\'ACTIVE\';')=='0'
        assert sql('SELECT count(*) FROM "ChargingJobs" WHERE "State"<>\'COMPLETED\';')=='0'
        assert sql('''SELECT count(*) FROM "Robots" WHERE "RobotType"<>'SIMULATION' OR ("RobotCode" IN ('R01','R02') AND "WarehouseID"<>'''+str(int(row['warehouse']))+');')=='0'
        verify_world(row['target'],timeout=45)
        finish(rid,'SUCCEEDED','Host recovery verified the retained backup, selected warehouse/world and both fresh camera positions. Dispatch is enabled; device incidents remain unchanged.')
        print('Verified recovery completed; selected world and original device incidents retained.')
        return
    if args.configure:
        if not sql('SELECT to_regclass(\'public."MapSwitchControl"\');'):
            saved = backup('before-web-map-manager-schema')
            sql((ROOT/'Database/20261127_web_map_switch.sql').read_text())
            print('Schema installed after database backup:', saved)
        host = cfg.get('MAP_SWITCH_HOST_ID') or str(uuid.uuid4())
        update_env({'MAP_SWITCH_HOST_ID': str(uuid.UUID(host))})
        print('Host identity and queue configured. Safely recreate backend before starting this worker.')
        return
    host = str(uuid.UUID(cfg['MAP_SWITCH_HOST_ID']))
    with single_worker():
        (private_dir() / 'map-switch-worker.pid').write_text(str(os.getpid()))
        # A worker crash must not turn an unverified world into a dispatchable one.
        interrupted = sql('''SELECT "Id" FROM "MapSwitchRequests" WHERE "HostId"=''' + quoted(host) + '''::uuid
            AND "State" NOT IN ('REQUESTED','SUCCEEDED','FAILED','FAILED_HOLD') LIMIT 1;''')
        if interrupted: finish(interrupted, 'FAILED_HOLD', 'Host worker restarted during a map change. Inspect the world and recover before releasing dispatch.')
        stop = threading.Event()
        def pulse():
            while not stop.is_set():
                try: heartbeat(host)
                except Exception: pass
                stop.wait(5)
        thread = threading.Thread(target=pulse, daemon=True); thread.start()
        pending_failure = None
        try:
            while True:
                try:
                    if pending_failure:
                        finish(pending_failure, 'FAILED_HOLD', 'Host processing failed. Inspect the saved world and recover before releasing dispatch.')
                        pending_failure = None
                    job = claim(host)
                    if job:
                        try: run_job(job)
                        except Exception:
                            pending_failure = job['id']
                            raise
                except Exception:
                    print('Map manager check failed; keeping existing database/world holds.', flush=True)
                    traceback.print_exc()
                time.sleep(3)
        finally: stop.set()


if __name__ == '__main__': main()
