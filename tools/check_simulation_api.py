"""Read-only/API access checks for the generated local Compose demo."""
import http.cookiejar
import json
import os
import time
import sys
from pathlib import Path
import urllib.error
import urllib.request

root=Path(__file__).resolve().parents[1]
env=dict(line.split('=',1) for line in (root/'.env').read_text().splitlines() if '=' in line)
base=os.environ.get('SMARTFLEET_DEMO_URL','http://127.0.0.1:'+env.get('WEB_PORT','8080'))


def request(path,token=None,data=None,method=None,expected=200,opener=None):
    headers={'Content-Type':'application/json'}
    if token:headers['Authorization']='Bearer '+token
    req=urllib.request.Request(base+path,headers=headers,data=None if data is None else json.dumps(data).encode(),method=method)
    try:
        with (opener.open(req) if opener else urllib.request.urlopen(req)) as response:
            code,body=response.status,response.read()
    except urllib.error.HTTPError as ex:code,body=ex.code,ex.read()
    assert code==expected,(path,code,body[:120])
    return body


for attempt in range(60):
    try:
        request('/healthz')
        break
    except Exception:
        if attempt==59:raise
        time.sleep(1)
summary=[]
for username,variable in [('admin','DEMO_ADMIN_PASSWORD'),('operator','DEMO_OPERATOR_PASSWORD'),('maintenance','DEMO_MAINTENANCE_PASSWORD')]:
    session=json.loads(request('/api/v1/auth/login',data={'usernameOrEmail':username,'password':env[variable]}))
    token=session['accessToken']
    saved=json.loads(request('/api/v1/navigation/map',token))
    assert saved['revision']>=5
    scene=json.loads(request('/api/v1/simulation',token))
    assert scene['mapCompatible']
    manifest_name='dual-lane-manifest.json' if scene['sceneId']=='smartfleet-dual-lane-r2025a-v2' else 'scene-manifest.json'
    commissioned=json.loads((root/'simulation/config'/manifest_name).read_text())
    asset='textures/dual-lane/QR1.png' if commissioned.get('directedLinks') else 'textures/QR_1.png'
    expected_robots={r['code'] for r in commissioned['robots']}
    # The HTTP process becomes healthy before Webots finishes loading the world.
    for attempt in range(60):
        poses=json.loads(request('/api/v1/simulation/poses',token))['poses']
        if {p['robotCode'] for p in poses}==expected_robots:break
        time.sleep(1)
    assert {p['robotCode'] for p in poses}==expected_robots, 'Webots did not publish all commissioned robot poses'
    if username=='maintenance':
        request('/api/v1/navigation/map',token,saved,'PUT',403)
        request('/api/v1/navigation/robots/R01/scan',token,{'currentArUcoId':0,'nextArUcoId':1},expected=403)
    opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    request('/api/v1/simulation/session',token,{},opener=opener)
    assert request('/api/v1/simulation/view/'+asset,opener=opener).startswith(b'\x89PNG')
    request('/api/v1/simulation/view/controllers/my_controller_testing_fleet/my_controller_testing_fleet.py',expected=404,opener=opener)
    request('/api/v1/simulation/view/%252e%252e/config/scene-manifest.json',expected=404,opener=opener)
    summary.append({'role':session['user']['roles'][0],'mapRevision':saved['revision'],'robots':len(poses),'assetWhitelist':True})
request('/api/v1/simulation/session',data={},expected=401)
request('/api/v1/simulation/view/'+asset,expected=401)
if '--map-write-check' in sys.argv:
    # Explicit opt-in: no active assignments, change/restore one marker yaw,
    # prove dispatch fails closed, and cancel the unassigned probe task.
    def login(name,key):
        return json.loads(request('/api/v1/auth/login',data={'usernameOrEmail':name,'password':env[key]}))['accessToken']
    admin=login('admin','DEMO_ADMIN_PASSWORD');operator=login('operator','DEMO_OPERATOR_PASSWORD')
    assert json.loads(request('/api/v1/task-interventions',operator))==[], 'Finish active demo assignments first'
    assert not json.loads(request('/api/v1/charging',operator))['jobs'], 'Finish/recover reserved charging cycles before the map-write probe'
    original=json.loads(request('/api/v1/navigation/map',admin));probe=None;edited=False
    changed=json.loads(json.dumps(original))
    marker=next(cell for row in changed['cells'] for cell in row if cell['type']=='qr')
    marker['markerYawDeg']+=45
    try:
        edited_map=json.loads(request('/api/v1/navigation/map',admin,changed,'PUT'));edited=True
        assert not json.loads(request('/api/v1/simulation',admin))['mapCompatible']
        probe=json.loads(request('/api/v1/transport-tasks',operator,{'warehouseId':1,'pickupStationId':1,
            'deliveryStationId':2,'priorityLevel':2,'payloadWeightKg':1,'dispatchMode':'MANUAL',
            },expected=201))['taskId']
        eligible=json.loads(request(f'/api/v1/transport-tasks/{probe}/available-robots?includeIneligible=true',operator))
        expected_codes={r['code'] for r in json.loads((root/'simulation/config/scene-manifest.json').read_text())['robots']}
        simulated=[r for r in eligible if r['robotCode'] in expected_codes]
        assert {r['robotCode'] for r in simulated}==expected_codes
        assert all(not r['isEligible'] for r in eligible)
        assert all('Simulation world' in r['ineligibilityReason'] for r in simulated)
    finally:
        if edited:
            original['revision']=json.loads(request('/api/v1/navigation/map',admin))['revision']
            restored=json.loads(request('/api/v1/navigation/map',admin,original,'PUT'))
        if probe:
            request(f'/api/v1/task-interventions/{probe}/cancel',operator,{'reason':'Completed map compatibility probe'})
    assert json.loads(request('/api/v1/simulation',admin))['mapCompatible']
    summary.append({'mapWriteRestoreRevision':restored['revision'],'mismatchedWorldDispatchBlocked':True})
print(json.dumps({'passed':True,'roles':summary}))
