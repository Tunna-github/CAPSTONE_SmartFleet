"""HTTP + PostgreSQL checks in a NEW isolated smartfleet-cancel-actions project.

Port 8089 only; never operates on the main demo or sends robot commands. Historical
assignment/verification rows below are explicit database fixtures, not robot ACKs.
"""
import json
import os
from pathlib import Path
import subprocess
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BASE = 'http://127.0.0.1:8089'
PROJECT = 'smartfleet-cancel-actions'
env = dict(line.split('=', 1) for line in (ROOT / '.env').read_text().splitlines() if '=' in line)


def api(path, token=None, data=None, method=None, expected=200):
    headers = {'Content-Type': 'application/json'}
    if token: headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request(BASE + path, headers=headers, method=method,
        data=json.dumps(data).encode() if data is not None else None)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            code, body = response.status, response.read()
    except urllib.error.HTTPError as error:
        code, body = error.code, error.read()
    assert code == expected, (path, code, body[:500])
    return json.loads(body) if body else None


def sql(query):
    return subprocess.run(['docker', 'compose', '-p', PROJECT, '-f', 'compose.yaml', '-f', 'compose.charging.yaml',
        'exec', '-T', 'database', 'psql', '-X', '-U', 'smartfleet', '-d', 'smartfleet', '-v', 'ON_ERROR_STOP=1', '-tA'],
        input=query, capture_output=True, text=True, check=True, cwd=ROOT).stdout.strip()


for attempt in range(60):
    try: api('/healthz'); break
    except Exception:
        if attempt == 59: raise
        time.sleep(1)
tokens = {role: api('/api/v1/auth/login', data={'usernameOrEmail': role,
    'password': env['DEMO_' + role.upper() + '_PASSWORD']})['accessToken'] for role in ['operator', 'admin', 'maintenance']}
token = tokens['operator']
assert api('/api/v1/transport-tasks', token) == [], 'Requires a NEW test database; never reset an existing installation'
catalog = api('/api/v1/warehouse-catalog', token)
stations = {s['arucoId']: s for s in catalog['stations'] if s['type'] == 'TRANSFER'}
created = []
for mode in ['MANUAL', 'AUTO']:
    task = api('/api/v1/transport-tasks', token, {'warehouseId': 1,
        'pickupStationId': stations[5]['stationId'], 'deliveryStationId': stations[1]['stationId'],
        'dispatchMode': mode, 'payloadWeightKg': 1, 'priorityLevel': 2,
        'itemDescription': 'Isolated cancelled task lifecycle acceptance'}, expected=201)
    tid = task['taskId']; created.append(tid)
    api(f'/api/v1/task-interventions/{tid}/cancel', token, {'reason': 'Isolated lifecycle test'})
    for role in ['admin', 'maintenance']:
        api(f'/api/v1/transport-tasks/{tid}/requeue', tokens[role],
            {'reason': 'Unauthorized', 'goodsAtPickupConfirmed': True}, expected=403)
        api(f'/api/v1/transport-tasks/{tid}/cancelled', tokens[role], method='DELETE', expected=403)
    api(f'/api/v1/transport-tasks/{tid}/requeue', token,
        {'reason': 'Not confirmed', 'goodsAtPickupConfirmed': False}, expected=400)
    result = api(f'/api/v1/transport-tasks/{tid}/requeue', token,
        {'reason': 'Goods at pickup for retry', 'goodsAtPickupConfirmed': True})
    task = api(f'/api/v1/transport-tasks/{tid}', token)
    assert task['taskStatus'] == 'QUEUED' and task['dispatchMode'] == mode and task['cancelledAt'] is None
    assert result['retryAt'] and task['scheduledTime']
    api(f'/api/v1/transport-tasks/{tid}/requeue', token,
        {'reason': 'Duplicate', 'goodsAtPickupConfirmed': True}, expected=409)
    api(f'/api/v1/task-interventions/{tid}/cancel', token, {'reason': 'Finished lifecycle fixture'})

# Explicit historical fixture: verify delete does not cascade operational evidence.
tid = created[0]
aid = int(sql(f'''INSERT INTO "TaskAssignments" ("TaskID","RobotID","AssignmentType","AssignmentStatus","EndedAt")
VALUES ({tid},2,'MANUAL','CANCELLED',now()) RETURNING "AssignmentID";''').splitlines()[0])
mid = int(sql(f'''INSERT INTO "Missions" ("MissionCode","AssignmentID","MissionStatus","EndExecutionTime")
VALUES ('ISOLATED-CANCEL-FIXTURE',{aid},'ABORTED',now()) RETURNING "MissionID";''').splitlines()[0])
sql(f'''INSERT INTO "DeliveryVerifications" ("MissionID","VerificationStage","VerificationType","IsVerified","VerifiedAt")
VALUES ({mid},'PICKUP','MANUAL',true,now());
UPDATE "TaskInterventions" SET "CommandId"='00000000-0000-0000-0000-000000000001',"CommandAcknowledged"=false WHERE "TaskId"={tid};''')
api(f'/api/v1/transport-tasks/{tid}/cancelled', token, method='DELETE', expected=409)
api(f'/api/v1/transport-tasks/{tid}/requeue', token,
    {'reason': 'Still waiting for stop', 'goodsAtPickupConfirmed': True}, expected=409)
sql(f'''UPDATE "TaskInterventions" SET "CommandAcknowledged"=true WHERE "TaskId"={tid};''')
api(f'/api/v1/transport-tasks/{tid}/cancelled', token, method='DELETE', expected=204)
api(f'/api/v1/transport-tasks/{tid}', token, expected=404)
api(f'/api/v1/transport-tasks/{tid}/requeue', token,
    {'reason': 'Already deleted', 'goodsAtPickupConfirmed': True}, expected=404)
assert tid not in [t['taskId'] for t in api('/api/v1/transport-tasks', token)]
assert sql(f'''SELECT count(*) FROM "Missions" m JOIN "DeliveryVerifications" v ON v."MissionID"=m."MissionID"
WHERE m."MissionID"={mid} AND m."MissionStatus"='ABORTED' AND v."IsVerified";''') == '1'
assert sql(f'''SELECT count(*) FROM "TransportTasks" WHERE "TaskID"={tid} AND "DeletedAt" IS NOT NULL AND "TaskStatus"='CANCELLED';''') == '1'
audits = int(sql('''SELECT count(*) FROM "SystemAuditLogs" WHERE "ActionName" IN ('REQUEUE_CANCELLED_TASK','DELETE_CANCELLED_TASK');'''))
assert audits == 3
report = {'passed': True, 'project': PROJECT, 'taskIds': created, 'manualAndAutoModePreserved': True,
    'unauthorizedRolesRejected': True, 'unacknowledgedCancellationRejected': True,
    'deletionKeepsMissionAndVerification': True, 'auditRecords': audits,
    'historicalRows': 'Isolated PostgreSQL fixtures; no Webots commands or actual robot ACKs generated'}
out = Path(os.environ['LOCALAPPDATA']) / 'SmartFleet/charging-validation/cancelled-task-actions.json'
out.write_text(json.dumps(report, indent=2))
print(json.dumps(report))
