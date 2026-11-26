"""Actual empty-leg cancellation + dock return in a fresh isolated Webots stack."""
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.request
import urllib.error

ROOT=Path(__file__).resolve().parents[1]
BASE='http://127.0.0.1:8089'
PROJECT='smartfleet-cancel-check'
OUT=Path(os.environ['LOCALAPPDATA'])/'SmartFleet/charging-validation/cancel-autoreturn'
OUT.mkdir(parents=True,exist_ok=True)
env=dict(line.split('=',1) for line in (ROOT/'.env').read_text().splitlines() if '=' in line)

def api(path,token=None,data=None,expected=200):
    headers={'Content-Type':'application/json'}
    if token:headers['Authorization']='Bearer '+token
    try:
        with urllib.request.urlopen(urllib.request.Request(BASE+path,headers=headers,data=None if data is None else json.dumps(data).encode()),timeout=15) as r:
            code=r.status;body=r.read()
    except urllib.error.HTTPError as ex:code=ex.code;body=ex.read()
    assert code==expected,(path,code,body[:500])
    return json.loads(body) if body else None

for attempt in range(60):
    try:api('/healthz');break
    except Exception:
        if attempt==59:raise
        time.sleep(1)
token=api('/api/v1/auth/login',data={'usernameOrEmail':'operator','password':env['DEMO_OPERATOR_PASSWORD']})['accessToken']
assert api('/api/v1/missions',token)==[],'Use a NEW isolated installation; never reset operational missions'
for _ in range(120):
    robots=api('/api/v1/robots',token)
    positions=api('/api/v1/demo-routing/robots/positions',token)
    if positions.get('R02')==67 and all(r['connectionStatus']=='ONLINE' for r in robots):break
    time.sleep(1)
assert positions.get('R02')==67
robot=next(r for r in robots if r['robotCode']=='R02')
catalog=api('/api/v1/warehouse-catalog',token)
stations={s['arucoId']:s for s in catalog['stations'] if s['type']=='TRANSFER'}

collector='''import json,time,paho.mqtt.client as mqtt
def receive(c,u,m):
 try:
  body=json.loads(m.payload)
  if 'motion' in body:print(json.dumps(body['motion']),flush=True)
 except Exception:pass
c=mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
c.on_message=receive
c.connect('mqtt',1883,30);c.subscribe('smartfleet/robot/R02/telemetry');c.loop_start()
time.sleep(600)
c.disconnect();c.loop_stop()
'''
process=subprocess.Popen(['docker','compose','-p',PROJECT,'-f','compose.yaml','-f','compose.charging.yaml','exec','-T','webots','python3','-u','-c',collector],
    stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,cwd=ROOT)
samples=[]
def consume():
    for line in process.stdout:
        try:samples.append(json.loads(line))
        except ValueError:pass
threading.Thread(target=consume,daemon=True).start()
try:
    task=api('/api/v1/transport-tasks',token,{'warehouseId':1,'pickupStationId':stations[5]['stationId'],
        'deliveryStationId':stations[2]['stationId'],'payloadWeightKg':1,'priorityLevel':2,'dispatchMode':'MANUAL',
        'itemDescription':'Isolated cancel-before-pickup regression'},201)
    task_id=task['taskId']
    api(f'/api/v1/transport-tasks/{task_id}/assign',token,{'robotId':robot['robotId']})
    cancelled=False;states=[];previous=None;deadline=time.monotonic()+1200
    while time.monotonic()<deadline:
        missions=api('/api/v1/missions',token);mission=next(m for m in missions if m['taskId']==task_id)
        interventions=api('/api/v1/task-interventions',token)
        row=next((r for r in interventions if r['taskId']==task_id),None)
        jobs=api('/api/v1/charging',token)['jobs']
        pose=next((p for p in api('/api/v1/simulation/poses',token)['poses'] if p['robotCode']=='R02'),None)
        state=(mission['missionStatus'],row.get('intervention',{}).get('state') if row and row.get('intervention') else None,
               jobs[0]['state'] if jobs else None,jobs[0]['nextWaypointIndex'] if jobs else None)
        if state!=previous:print(state,flush=True);states.append(state);previous=state
        assert mission['missionStatus']!='FAILED',mission
        assert not any(j['state']=='FAILED' for j in jobs),jobs
        if not cancelled and pose and pose['y']>16.8 and 2.5<pose['x']<4.3:
            api(f'/api/v1/task-interventions/{task_id}/cancel',token,{'reason':'Isolated test: cancel moving empty leg before pickup','expectedAssignmentId':mission['assignmentId']})
            cancelled=True;print('Cancelled between QR64 and QR65; no synthetic waypoint reports',flush=True)
        if cancelled and mission['missionStatus']=='ABORTED' and any(j['state']=='READY' for j in jobs):
            assert mission['assignmentStatus']=='CANCELLED' and not mission['pickupVerified']
            moving=[m for m in samples if abs(m.get('leftCommand',0))>5.5 and abs(m.get('rightCommand',0))>5.5]
            assert moving,'Must observe actual high-speed wheel commands'
            measured=[m for m in moving if abs(m.get('leftMeasuredRadPerSimSecond',0))>5]
            assert measured,'Encoder measurements must confirm the wheel speed'
            report={'passed':True,'taskId':task_id,'missionId':mission['missionId'],'cancelledBetweenCheckpoints':True,
                'mission':mission,'finalCharging':jobs,'states':states,'motionSamples':samples,
                'peakLeftCommand':max(abs(m.get('leftCommand',0)) for m in samples),
                'peakMeasuredLeftWheelRadPerSecond':max(abs(m.get('leftMeasuredRadPerSimSecond',0)) for m in measured)}
            (OUT/'cancel-autoreturn.json').write_text(json.dumps(report,indent=2))
            print(json.dumps({k:v for k,v in report.items() if k not in ['mission','finalCharging','states','motionSamples']}),flush=True)
            break
        time.sleep(1)
    else:raise TimeoutError(previous)
finally:
    process.terminate()
