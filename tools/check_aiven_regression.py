"""Opt-in shared DEMO regression: scheduled AUTO, real cancel/charging, requeue/delete.

Creates one clearly labelled test task and retains its audit/history after logical
deletion. Requires Map 2, idle R02 camera-localized at QR67, and no active work.
Never sends synthetic arrival, motion or device-ack messages.
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import time
from aiven_support import private_dir, read_env, sql
from select_aiven_map import api


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run-demo-test', action='store_true', required=True)
    parser.parse_args()
    cfg = read_env()
    token = api('/api/v1/auth/login', data={'usernameOrEmail': 'operator', 'password': cfg['DEMO_OPERATOR_PASSWORD']})['accessToken']
    assert api('/api/v1/map-profiles', token)['activeKey'] == 'map2'
    assert not any(m['assignmentStatus'] == 'ACTIVE' for m in api('/api/v1/missions', token)), 'Do not interrupt active work'
    assert api('/api/v1/demo-routing/robots/positions', token).get('R02') == 67
    catalog = api('/api/v1/warehouse-catalog', token)
    assert any(w['code'] == 'SIM' and w['warehouseId'] == 1 for w in catalog['warehouses'])
    stations = {s['arucoId']: s for s in catalog['stations'] if s['isActive'] and s['type'] == 'TRANSFER'}
    robot = next(r for r in api('/api/v1/robots', token) if r['robotCode'] == 'R02')
    assert robot['operationalStatus'] == 'AVAILABLE' and robot['connectionStatus'] == 'ONLINE'
    task = api('/api/v1/transport-tasks', token, {'warehouseId': 1, 'pickupStationId': stations[5]['stationId'],
        'deliveryStationId': stations[1]['stationId'], 'payloadWeightKg': 1, 'priorityLevel': 2, 'dispatchMode': 'AUTO',
        'scheduledTime': (datetime.now(timezone.utc) + timedelta(seconds=15)).isoformat(),
        'itemDescription': '[Validation] Aiven scheduled AUTO / real empty cancel / requeue / logical delete'})
    tid = task['taskId']; print('Created scheduled AUTO test task', tid, flush=True)
    time.sleep(3)
    assert api(f'/api/v1/transport-tasks/{tid}', token)['taskStatus'] == 'QUEUED', 'Scheduled task dispatched early'
    deadline = time.monotonic() + 900; cancelled = False; states = []; previous = None
    while time.monotonic() < deadline:
        mission = next((m for m in api('/api/v1/missions', token) if m['taskId'] == tid), None)
        if mission:
            assert mission['missionStatus'] != 'FAILED', mission
            jobs = api('/api/v1/charging', token)['jobs']
            job = next((j for j in jobs if j['sourceMissionId'] == mission['missionId']), None)
            if job: assert job['state'] != 'FAILED', job['reason']
            state = (mission['missionStatus'], job['state'] if job else None, job['nextWaypointIndex'] if job else None)
            if state != previous: previous = state; states.append(state); print(state, flush=True)
            poses = api('/api/v1/simulation/poses', token)['poses']
            pose = next((p for p in poses if p['robotCode'] == 'R02'), None)
            if not cancelled and pose and pose['y'] > 16.8 and 2.5 < pose['x'] < 4.3:
                api(f'/api/v1/task-interventions/{tid}/cancel', token, {
                    'reason': 'Acceptance test: cancel empty AUTO task between QR64 and QR65',
                    'expectedAssignmentId': mission['assignmentId']})
                cancelled = True; print('Real empty-leg cancellation requested', flush=True)
            if cancelled and mission['missionStatus'] == 'ABORTED' and job and job['state'] == 'READY':
                assert mission['assignmentStatus'] == 'CANCELLED' and not mission['pickupVerified']
                break
        time.sleep(1)
    else: raise RuntimeError('AUTO/cancel return did not complete: ' + repr(previous))
    assert sql(f'''SELECT "AssignmentType" FROM "TaskAssignments" WHERE "TaskID"={int(tid)} ORDER BY "AssignmentID" DESC LIMIT 1;''') == 'AUTO'
    api(f'/api/v1/transport-tasks/{tid}/requeue', token, {
        'reason': 'Acceptance: virtual stock remains at original pickup', 'goodsAtPickupConfirmed': True})
    row = api(f'/api/v1/transport-tasks/{tid}', token)
    assert row['taskStatus'] == 'QUEUED' and row['dispatchMode'] == 'AUTO'
    # Cancel again inside the new 30s scheduling window; never dispatch a second test attempt.
    api(f'/api/v1/task-interventions/{tid}/cancel', token, {'reason': 'Acceptance finished; retain history and remove test from operations'})
    import urllib.request
    request = urllib.request.Request('http://127.0.0.1:' + cfg.get('WEB_PORT', '8080') + f'/api/v1/transport-tasks/{tid}/cancelled',
        headers={'Authorization': 'Bearer ' + token}, method='DELETE')
    with urllib.request.urlopen(request, timeout=20) as response: assert response.status == 204
    assert tid not in [t['taskId'] for t in api('/api/v1/transport-tasks', token)]
    assert sql(f'''SELECT count(*) FROM "Missions" WHERE "MissionID"={int(mission['missionId'])} AND "MissionStatus"='ABORTED';''') == '1'
    report = dict(passed=True, taskId=tid, missionId=mission['missionId'], states=states, scheduledAuto=True,
        cameraConfirmedCancelReturn=True, requeuedModePreserved=True, logicallyDeleted=True, historyRetained=True)
    (private_dir() / 'aiven-auto-cancel-regression.json').write_text(json.dumps(report, indent=2))
    print(json.dumps({k: v for k, v in report.items() if k != 'states'}), flush=True)


if __name__ == '__main__': main()
