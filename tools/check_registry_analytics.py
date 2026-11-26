"""FE-03/08 acceptance against an explicitly selected isolated Compose installation.

Writes test registrations, then deactivates them; preserves identity/audit history.
No task dispatch, robot movement, telemetry fabrication or operational DB reset.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import io
import json
import os
from pathlib import Path
import urllib.error
import urllib.request
import uuid
import zipfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--write', action='store_true', help='Confirm this is an isolated writable test installation')
args = parser.parse_args()
assert args.write, 'Use --write only against isolated test data'
base = args.url.rstrip('/')
root = Path(__file__).resolve().parents[1]
env = dict(l.split('=', 1) for l in (root / '.env').read_text().splitlines() if '=' in l)
output = Path(os.environ.get('SMARTFLEET_EVIDENCE_DIR', root / 'test-results/registry-analytics'))
output.mkdir(parents=True, exist_ok=True)


def call(path, token=None, body=None, method=None, expected=(200,)):
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), method=method,
        headers={'Content-Type': 'application/json', **({'Authorization': 'Bearer ' + token} if token else {})})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            status, data, content = response.status, response.read(), response.headers.get('Content-Type', '')
    except urllib.error.HTTPError as error:
        status, data, content = error.code, error.read(), error.headers.get('Content-Type', '')
    assert status in expected, (path, status, data[:500])
    return status, json.loads(data) if 'json' in content and data else data


tokens = {role: call('/api/v1/auth/login', body={'usernameOrEmail': role, 'password': env[key]})[1]['accessToken']
    for role, key in [('admin', 'DEMO_ADMIN_PASSWORD'), ('operator', 'DEMO_OPERATOR_PASSWORD'), ('maintenance', 'DEMO_MAINTENANCE_PASSWORD')]}
admin, operator, maintenance = (tokens[r] for r in ['admin', 'operator', 'maintenance'])
call('/api/v1/robot-registry', expected=(401,))
call('/api/v1/analytics', expected=(401,))
for token in [operator, maintenance]:
    call('/api/v1/robot-registry', token, expected=(403,))
    call('/api/v1/robot-registry', token, {}, expected=(403,))
call('/api/v1/analytics', maintenance, expected=(403,))
call('/api/v1/analytics/export/pdf', maintenance, expected=(403,))
call('/api/v1/analytics/export/xlsx', maintenance, expected=(403,))
registry = call('/api/v1/robot-registry', admin)[1]
warehouse = call('/api/v1/warehouse-catalog', admin)[1]['navigationWarehouseId']
baseline = sum(r['warehouseId'] == warehouse for r in registry['robots'])
nonce = uuid.uuid4().hex[:10].upper()
body = dict(warehouseId=warehouse, robotCode='TEST-' + nonce, robotType='PHYSICAL', model='Acceptance JetRacer',
    controlSoftware='ROS_AGENT', robotAgentVersion='test', ipAddress='127.0.0.1', macAddress='AA:BB:CC:11:22:33',
    mqttClientIdentifier='registry-' + nonce, maxPayloadWeightKg=10, isActive=True, registryRevision=0)
for invalid in [{'robotCode': 'invalid/+'}, {'maxPayloadWeightKg': 0}, {'ipAddress': 'not-an-ip'}, {'warehouseId': 2147483647}]:
    call('/api/v1/robot-registry', admin, {**body, **invalid}, expected=(400,))
robot_id = call('/api/v1/robot-registry', admin, {**body, 'connectionStatus': 'ONLINE', 'batteryPercent': 100})[1]['robotId']
created = next(r for r in call('/api/v1/robot-registry', admin)[1]['robots'] if r['robotId'] == robot_id)
assert created['connectionStatus'] == 'OFFLINE' and created['batteryPercent'] is None and created['lastHeartbeatTime'] is None
call('/api/v1/robot-registry', admin, {**body, 'robotCode': body['robotCode'].lower(), 'mqttClientIdentifier': 'other-' + nonce}, expected=(409,))
call('/api/v1/robot-registry', admin, {**body, 'robotCode': 'OTHER-' + nonce, 'mqttClientIdentifier': body['mqttClientIdentifier'].upper()}, expected=(409,))
call(f'/api/v1/robot-registry/{robot_id}', operator, body, 'PUT', expected=(403,))
updated = {**body, 'maxPayloadWeightKg': 12, 'registryRevision': 1}
call(f'/api/v1/robot-registry/{robot_id}', admin, updated, 'PUT')
call(f'/api/v1/robot-registry/{robot_id}', admin, updated, 'PUT', expected=(409,))
call(f'/api/v1/robot-registry/{robot_id}', admin, {**updated, 'registryRevision': 2, 'robotCode': 'RENAMED-' + nonce}, 'PUT', expected=(409,))
call(f'/api/v1/robot-registry/{robot_id}', admin, {**updated, 'registryRevision': 2, 'isActive': False}, 'PUT')


def race(index):
    return call('/api/v1/robot-registry', admin, {**body, 'robotCode': 'RACE-' + nonce, 'mqttClientIdentifier': f'race-{nonce}-{index}'}, expected=(200, 409))


with ThreadPoolExecutor(max_workers=2) as pool:
    results = list(pool.map(race, [1, 2]))
assert sorted(r[0] for r in results) == [200, 409], results
winner = next(r[1]['robotId'] for r in results if r[0] == 200)
row = next(r for r in call('/api/v1/robot-registry', admin)[1]['robots'] if r['robotId'] == winner)
names = ['warehouseId', 'robotCode', 'robotType', 'model', 'controlSoftware', 'robotAgentVersion', 'ipAddress', 'macAddress', 'mqttClientIdentifier', 'maxPayloadWeightKg', 'isActive', 'registryRevision']
call(f'/api/v1/robot-registry/{winner}', admin, {**{k: row[k] for k in names}, 'isActive': False}, 'PUT')
today = datetime.now(timezone(timedelta(hours=7))).date()
day = today.isoformat()
query = f'?from={day}&to={day}&warehouseId={warehouse}'
report = call('/api/v1/analytics' + query, operator)[1]
assert report['fleet']['registered'] == baseline + 2
assert all(r['utilizationPercent'] is None or 0 <= r['utilizationPercent'] <= 100 for r in report['robotUtilization'])
assert report['tasks']['created'] == sum(report['tasks'][k] for k in ['completed', 'failed', 'cancelled', 'other'])
assert report['tasks']['completedInPeriod'] == sum(d['completedTasks'] for d in report['daily'])
empty = call('/api/v1/analytics' + query + f'&robotId={robot_id}', admin)[1]
assert empty['tasks']['created'] == 0 and empty['tasks']['successRatePercent'] is None and empty['tasks']['averageCompletionSeconds'] is None
for bad in [f'?from={day}&to={today-timedelta(days=1)}', f'?from={day}&to={today+timedelta(days=1)}', '?warehouseId=2147483647', '?from=invalid']:
    call('/api/v1/analytics' + bad, admin, expected=(400,))
call('/api/v1/analytics/export/csv', admin, expected=(400,))
xlsx = call('/api/v1/analytics/export/xlsx' + query, operator)[1]
with zipfile.ZipFile(io.BytesIO(xlsx)) as archive:
    assert len([n for n in archive.namelist() if n.startswith('xl/worksheets/')]) == 4
    for n in archive.namelist():
        if n.endswith('.xml') or n.endswith('.rels'):
            ET.fromstring(archive.read(n))
pdf = call('/api/v1/analytics/export/pdf' + query, admin)[1]
assert pdf.startswith(b'%PDF-1.4') and pdf.endswith(b'%%EOF\n')
(output / 'report.xlsx').write_bytes(xlsx)
(output / 'report.pdf').write_bytes(pdf)
(output / 'analytics.json').write_text(json.dumps(report, indent=2))
(output / 'acceptance.json').write_text(json.dumps({'registeredRobotId': robot_id, 'concurrentWinnerId': winner,
    'newDeviceOffline': True, 'duplicateAndConcurrencyProtected': True, 'staleRevisionRejected': True,
    'roleChecksPassed': True, 'dateValidationPassed': True, 'exports': ['xlsx', 'pdf'], 'testRegistrationsDeactivated': True}, indent=2))
print('PASS: PostgreSQL registration, case-insensitive duplicates, concurrent registration, revision checks, role restrictions, analytics and both exports.', flush=True)
