"""Real two-robot test, restricted to the isolated charging stack on port 8089.
No test telemetry supplies navigation/arrival evidence. Operator verification is
automated only for this simulation test, using each mission's frozen codes.
"""
import json
import math
import os
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid
import argparse

ROOT=Path(__file__).resolve().parents[1]
BASE='http://127.0.0.1:8089'
ENV=dict(line.split('=',1) for line in (ROOT/'.env').read_text().splitlines() if '=' in line)
OUT=Path(os.environ.get('SMARTFLEET_EVIDENCE_DIR',str(Path(os.environ['LOCALAPPDATA'])/'SmartFleet/charging-validation')))
OUT.mkdir(parents=True,exist_ok=True)
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--recover-fixture',type=Path,help='Explicit isolated-world physical recovery fixture; never clears DB resource holds')
parser.add_argument('--resume-progress',type=Path,help='Resume monitoring an existing isolated acceptance run without restarting robots')
args=parser.parse_args()


def call(path,data=None,token=None,expected=(200,201)):
    headers={'Content-Type':'application/json'}
    if token: headers['Authorization']='Bearer '+token
    request=urllib.request.Request(BASE+path,data=json.dumps(data).encode() if data is not None else None,headers=headers)
    try:
        with urllib.request.urlopen(request,timeout=15) as r: status,body=r.status,r.read()
    except urllib.error.HTTPError as e: status,body=e.code,e.read()
    assert status in expected,(path,status,body.decode()[:600])
    return json.loads(body) if body else None


def login(role): return call('/api/v1/auth/login',{'usernameOrEmail':role,'password':ENV['DEMO_'+role.upper()+'_PASSWORD']})['accessToken']


def main():
    for _ in range(120):
        try:token=login('operator');break
        except AssertionError as ex:
            if not isinstance(ex.args[0],tuple) or ex.args[0][1] not in (502,503):raise
            time.sleep(1)
    else:raise RuntimeError('Isolated backend did not become ready')
    maintenance=login('maintenance')
    for _ in range(180):
        scene=call('/api/v1/simulation',token=token)
        if scene['mapCompatible'] and len(call('/api/v1/demo-routing/robots/positions',token=token))==2:break
        time.sleep(1)
    assert scene['mapCompatible'] and scene['sceneId']=='smartfleet-dual-lane-r2025a-v2',scene
    assert not any(m['assignmentStatus']=='ACTIVE' for m in call('/api/v1/missions',token=token)), 'Fresh isolated test stack required'
    if not args.recover_fixture and not args.resume_progress:assert not call('/api/v1/charging',token=token)['jobs'],'Finish previous charging test first'
    catalog=call('/api/v1/warehouse-catalog',token=token)
    station={s['arucoId']:s['stationId'] for s in catalog['stations'] if s['type']=='TRANSFER'}
    robots={r['robotCode']:r for r in call('/api/v1/robots',token=token)}
    tasks=[];confirmed=set();events=[];last={};points=[];requeue_checked=False;second=None;paused=False;resumed=False;auto_created=False
    min_separation=999.
    blocked_task=None
    def create(a,b,code,assign=True,auto=False):
        t=call('/api/v1/transport-tasks',{'warehouseId':1,'pickupStationId':station[a],'deliveryStationId':station[b],
            'payloadWeightKg':1,'priorityLevel':2,'dispatchMode':'AUTO' if auto else 'MANUAL','itemDescription':'Dual lane charging integration test'},token)
        if assign: call(f'/api/v1/transport-tasks/{t["taskId"]}/assign',{'robotId':robots[code]['robotId']},token)
        tasks.append(t['taskId']);return t['taskId']
    if args.resume_progress:
        progress=json.loads(args.resume_progress.read_text());tasks.extend(progress['tasks']);events.extend(progress['events'])
        min_separation=progress['minRobotSeparationM'];second=tasks[-1];auto_created=True
        blocked_task=progress.get('queuedManualTaskId',3)
        paused=any(e.get('robot')=='R02' and e.get('state',[None])[0]=='PAUSED' for e in events)
        resumed=paused and any(j['robotCode']=='R02' and j['state']=='READY' for j in call('/api/v1/charging',token=token)['jobs'])
        assert paused and resumed,'Resume requires the recorded real Pause/recovery phase'
        for m in call('/api/v1/missions',token=token):
            if m['taskId'] in tasks:
                for stage,key in [('PICKUP','pickupVerified'),('DELIVERY','deliveryVerified')]:
                    if m[key]:confirmed.add((m['missionId'],stage))
    elif args.recover_fixture:
        fixture=json.loads(args.recover_fixture.read_text())
        assert fixture['project']=='smartfleet-charging-final' and fixture['sceneId']==scene['sceneId']
        actual=call('/api/v1/demo-routing/robots/positions',token=token)
        assert actual==fixture['recoveredCheckpoints'],(actual,fixture['recoveredCheckpoints'])
        old=call('/api/v1/missions',token=token)
        tasks.extend(fixture['taskIds']);second=fixture['queuedManualTaskId'];tasks.append(second);blocked_task=second
        for m in old:
            if m['taskId'] in tasks:
                assert m['missionStatus']=='COMPLETED' and m['pickupVerified'] and m['deliveryVerified']
                confirmed.update([(m['missionId'],'PICKUP'),(m['missionId'],'DELIVERY')])
        for j in call('/api/v1/charging',token=token)['jobs']:
            call(f'/api/v1/charging/{j["id"]}/pause',{'reason':'Explicit simulation fixture: stopped recovery at last camera-confirmed QR'},maintenance)
        for r in call('/api/v1/robots',token=token):
            for incident in r['openIncidents']:
                call(f'/api/v1/robots/{r["robotId"]}/incidents/{incident["incidentId"]}/resolve',
                    {'resolutionNote':'Isolated simulation: inspected controller timing fix; physically recovered to last confirmed checkpoint'},maintenance)
        for _ in range(90):
            jobs=call('/api/v1/charging',token=token)['jobs']
            if all(j.get('stopAcknowledgedAt') for j in jobs):break
            time.sleep(1)
        assert all(j.get('stopAcknowledgedAt') for j in jobs),'Real controller stop ACK is required'
        for j in jobs:
            call(f'/api/v1/charging/{j["id"]}/resume',{'reason':'Physically inspected simulation recovery fixture; camera localized at confirmed QR','physicalSafetyConfirmed':True},maintenance)
        print('PASS: persistent fault/berth/corridor holds recovered by Maintenance after actual fresh camera and stop ACK',flush=True)
    else:
        create(0,6,'R01');create(4,5,'R02')
    deadline=time.monotonic()+3600
    while time.monotonic()<deadline:
        missions=[m for m in call('/api/v1/missions',token=token) if m['taskId'] in tasks]
        jobs=call('/api/v1/charging',token=token)['jobs']
        pose=call('/api/v1/simulation/poses',token=token)['poses'];points.extend(pose)
        if len(pose)==2:
            distance=math.hypot(pose[0]['x']-pose[1]['x'],pose[0]['y']-pose[1]['y'])*.5
            min_separation=min(min_separation,distance)
            assert distance>.22,('Robots too close',distance,pose)
        for m in missions:
            key='M'+str(m['missionId']);state=(m['missionStatus'],m['nextWaypointIndex'])
            if last.get(key)!=state:
                print(key,m['robotCode'],state,flush=True);events.append({'mission':key,'robot':m['robotCode'],'state':state});last[key]=state
            assert m['missionStatus'] not in ('FAILED','ABORTED','CANCELLED'),m
            stage='PICKUP' if m['missionStatus']=='AT_PICKUP' else 'DELIVERY' if m['missionStatus']=='AT_DELIVERY' else None
            if stage and m['stationPresenceFresh'] and (m['missionId'],stage) not in confirmed:
                code=m['pickupVerificationCode'] if stage=='PICKUP' else m['deliveryVerificationCode']
                result=call(f'/api/v1/missions/{m["missionId"]}/verifications',{'requestId':str(uuid.uuid4()),'stage':stage,'type':'QRCODE',
                    'scannedQr':code,'physicalTransferConfirmed':True},token)
                assert result['accepted'];confirmed.add((m['missionId'],stage))
        for j in jobs:
            key='C'+j['id'];state=(j['state'],j['nextWaypointIndex'])
            if last.get(key)!=state:
                print(j['robotCode'],'charging',state,flush=True);events.append({'charging':key,'robot':j['robotCode'],'state':state});last[key]=state
            assert j['state'] not in ('FAILED','PAUSED') or j['robotCode']=='R02' and paused and not resumed,j
        if not requeue_checked and any(j['robotCode']=='R01' and j['state']=='RETURNING' for j in jobs):
            if blocked_task is None:blocked_task=create(0,5,'R01',False)
            if second is None:second=blocked_task
            eligible=call(f'/api/v1/transport-tasks/{blocked_task}/available-robots?includeIneligible=true',token=token)
            assert not next(r for r in eligible if r['robotCode']=='R01')['isEligible']
            call(f'/api/v1/transport-tasks/{blocked_task}/assign',{'robotId':robots['R01']['robotId']},token,expected=(409,))
            requeue_checked=True;print('PASS: dispatch blocked before camera-confirmed berth arrival',flush=True)
        if not paused and len(jobs)==2 and all(j['state']=='READY' for j in jobs):
            assert len({j['stationId'] for j in jobs})==2
            call('/api/v1/navigation/map',token=token) # no fake simulation scan
            j=next(j for j in jobs if j['robotCode']=='R02')
            call(f'/api/v1/charging/{j["id"]}/resume',{'reason':'Unauthorized operator resume','physicalSafetyConfirmed':True},token,expected=(403,))
            call(f'/api/v1/charging/{j["id"]}/pause',{'reason':'Simulation test safe dock pause'},token)
            paused=True;print('PASS: distinct charging bays, operator Pause and Maintenance-only resume',flush=True)
        if paused and not resumed:
            j=next((j for j in jobs if j['robotCode']=='R02' and j['state']=='PAUSED'),None)
            if j and j.get('stopAcknowledgedAt') and call('/api/v1/demo-routing/robots/positions',token=token).get('R02') in [67,68,69,70]:
                # Ensure the STOP command has reached the controller before resuming.
                time.sleep(3)
                call(f'/api/v1/charging/{j["id"]}/resume',{'reason':'Inspected stopped robot and clear lane','physicalSafetyConfirmed':True},maintenance)
                resumed=True
        if second and not auto_created and resumed and any(j['robotCode']=='R01' and j['state']=='READY' for j in jobs):
            tasks.remove(second) # leave the manual eligibility fixture QUEUED
            second=create(5,0,'R01',False,True);auto_created=True
            print('AUTO task queued from charging berth; release must wait for camera-confirmed exit',flush=True)
        (OUT/'charging-progress.json').write_text(json.dumps(dict(tasks=tasks,events=events,jobs=jobs,missions=missions,minRobotSeparationM=min_separation,queuedManualTaskId=blocked_task,startedFromRecovery=bool(args.recover_fixture) or bool(args.resume_progress)),indent=2))
        if second and len(missions)==3 and all(m['missionStatus']=='COMPLETED' for m in missions) and len(jobs)==2 and all(j['state']=='READY' for j in jobs) and resumed:
            report=dict(tasks=tasks,events=events,minRobotSeparationM=min_separation,poseSamples=len(points),confirmations=len(confirmed),
                blockedUntilDocked=requeue_checked,pausedAndResumed=resumed,jobs=jobs,missions=missions,startedFromRecovery=bool(args.recover_fixture) or bool(args.resume_progress))
            (OUT/'charging-integration.json').write_text(json.dumps(report,indent=2))
            print('PASS: 3 actual missions, return/dock/redispatch, two bays, pause/recovery; evidence at',OUT,flush=True)
            return
        time.sleep(1)
    raise TimeoutError(last)


if __name__=='__main__':main()
