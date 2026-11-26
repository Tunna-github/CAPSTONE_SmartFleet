"""End-to-end demo check: real controller movement + FE07 operator confirmations.

Creates AUTO test tasks for commissioned station pairs in the isolated Compose demo
database (one pair on a fresh seed). Never targets Aiven.
"""
import json
import argparse
import os
import sys
from pathlib import Path
import time
import urllib.error
import urllib.request
from urllib.parse import urlparse
import uuid
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--single', action='store_true')
parser.add_argument('--manual', action='store_true')
parser.add_argument('--robot-code', default='R02', help='Robot selected for MANUAL checks')
parser.add_argument('--pickup-station-id', type=int)
parser.add_argument('--delivery-station-id', type=int)
parser.add_argument('--cutover-project', help='Isolated station test project: upgrade catalog codes while stopped at pickup')
args = parser.parse_args()
assert bool(args.pickup_station_id) == bool(args.delivery_station_id), 'Supply both station IDs'

root = Path(__file__).resolve().parents[1]
env = dict(line.split("=", 1) for line in (root / ".env").read_text().splitlines() if "=" in line)
base = os.environ.get("SMARTFLEET_DEMO_URL", "http://127.0.0.1:" + env.get("WEB_PORT", "8080"))
if args.cutover_project:
    assert args.cutover_project.startswith('smartfleet-station-') and urlparse(base).port == 8086
    assert args.manual, 'Cutover checks require a single manual test mission'
token = None


def call(path, data=None, authorized=True, expected=(200, 201)):
    headers = {"Content-Type": "application/json"}
    if authorized and token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(), headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=12) as response:
            status = response.status
            body = response.read()
    except urllib.error.HTTPError as ex:
        status, body = ex.code, ex.read()
    if status not in expected:
        raise RuntimeError(f"{path}: HTTP {status}: {body.decode()[:500]}")
    return json.loads(body) if body else None


session = call("/api/v1/auth/login", {"usernameOrEmail": "operator", "password": env["DEMO_OPERATOR_PASSWORD"]}, False)
token = session["accessToken"]
assert call("/api/v1/simulation")["mapCompatible"]
call("/api/v1/simulation/session", {}, False, (401,))
call("/api/v1/simulation/bridge/register", {"runId": str(uuid.uuid4()), "worldSha256": "invalid"}, False, (401,))
before=[]
for _ in range(120):
    before=call("/api/v1/simulation/poses")["poses"]
    if len(before)==2 and len(call("/api/v1/demo-routing/robots/positions"))==2:
        break
    time.sleep(1)
assert len(before) == 2, "Both simulated robots must localize within 120 seconds"
existing = call("/api/v1/missions")
assert not any(m["assignmentStatus"] == "ACTIVE" for m in existing), "Finish existing demo tasks before running this check"
tasks = []
catalog = call('/api/v1/warehouse-catalog')
pickups = [s for s in catalog['stations'] if s['usable'] and s['type'] in ('PICKUP','TRANSFER')]
deliveries = [s for s in catalog['stations'] if s['usable'] and s['type'] in ('DELIVERY','TRANSFER')]
# Keep a bounded smoke scenario; transfer points can appear in either list.
preferred_pickup = next((s for s in pickups if s['code']=='P1'), pickups[0] if pickups else None)
preferred_delivery = next((s for s in deliveries if s['code']=='D1' and s['stationId']!=preferred_pickup['stationId']), None) if preferred_pickup else None
if preferred_pickup and not preferred_delivery:
    preferred_delivery = next((s for s in deliveries if s['stationId']!=preferred_pickup['stationId']), None)
routes = [(preferred_pickup,preferred_delivery)] if preferred_pickup and preferred_delivery else []
if args.pickup_station_id:
    routes = [(next(s for s in pickups if s['stationId']==args.pickup_station_id), next(s for s in deliveries if s['stationId']==args.delivery_station_id))]
assert routes, 'Configure active mapped pickup/delivery stations first'
if args.single or args.manual: routes = routes[:1]
task_stations = {}
for pickup, delivery in routes:
    assert pickup['warehouseId']==delivery['warehouseId']
    task = call("/api/v1/transport-tasks", {"warehouseId": pickup['warehouseId'], "pickupStationId": pickup['stationId'], "deliveryStationId": delivery['stationId'],
        "payloadWeightKg": 1, "priorityLevel": 2, "dispatchMode": "MANUAL" if args.manual else "AUTO", "itemDescription": "Compose integration check"})
    task_stations[task['taskId']] = (pickup,delivery)
    tasks.append(task["taskId"])
    if args.manual:
        available = call(f'/api/v1/transport-tasks/{task["taskId"]}/available-robots?includeIneligible=true')
        selected = next(r for r in available if r['robotCode']==args.robot_code)
        assert selected['isEligible'],selected.get('ineligibilityReason')
        assigned = call(f'/api/v1/transport-tasks/{task["taskId"]}/assign',{'robotId':selected['robotId']})
        print('MANUAL assignment accepted for '+args.robot_code,flush=True)
print("Created demo tasks", tasks, flush=True)
events = []
states = {}
waypoint_cursors = {}
confirmed = set()
wrong_checked = False
cutover_checked = False
poses = []
deadline = time.monotonic() + 600
while time.monotonic() < deadline:
    missions = [m for m in call("/api/v1/missions") if m["taskId"] in tasks]
    poses.extend(call("/api/v1/simulation/poses")["poses"])
    for mission in missions:
        mid = mission["missionId"]
        status = mission["missionStatus"]
        if states.get(mid) != status:
            states[mid] = status
            events.append({"missionId": mid, "robotCode": mission["robotCode"], "status": status})
            print("Mission", mid, mission["robotCode"], status, flush=True)
        cursor = (mission.get('nextWaypointIndex'), mission.get('lastReachedArUcoId'))
        if waypoint_cursors.get(mid) != cursor:
            waypoint_cursors[mid] = cursor
            print('Mission', mid, 'next waypoint', cursor[0], 'last camera QR', cursor[1], flush=True)
        assert status not in ("FAILED", "CANCELLED"), f"Mission failed: {mid}"
        stage = "PICKUP" if status == "AT_PICKUP" else "DELIVERY" if status == "AT_DELIVERY" else None
        if stage and (mid, stage) not in confirmed and mission["stationPresenceFresh"]:
            if args.cutover_project and not cutover_checked:
                before_codes = (mission['pickupVerificationCode'], mission['deliveryVerificationCode'])
                assert before_codes == ('P1', 'D1'), before_codes
                subprocess.run(['docker', 'compose', '-p', args.cutover_project, 'exec', '-T', 'database',
                    'psql', '-X', '-v', 'ON_ERROR_STOP=1', '-U', 'smartfleet', '-d', 'smartfleet'],
                    input=(root/'deploy/unify-demo-verification-codes.sql').read_text(),text=True,capture_output=True,check=True,cwd=root)
                updated = call(f'/api/v1/missions/{mid}')
                assert (updated['pickupVerificationCode'],updated['deliveryVerificationCode']) == before_codes
                assert updated['waypoints'] == mission['waypoints']
                latest = call('/api/v1/warehouse-catalog')
                assert next(s for s in latest['stations'] if s['arucoId']==2)['qrCodePayload']=='QR2'
                assert next(s for s in latest['stations'] if s['arucoId']==7)['qrCodePayload']=='QR7'
                cutover_checked = True
                print('PASS: live catalog cutover; active mission keeps frozen P1/D1 and route.',flush=True)
            verification = {"requestId": str(uuid.uuid4()), "stage": stage, "type": "QRCODE",
                "scannedQr": mission['pickupVerificationCode'] if stage=='PICKUP' else mission['deliveryVerificationCode'], "scannedPackageCode": None,
                "physicalTransferConfirmed": True, "note": None}
            if not wrong_checked:
                station_code = task_stations[mission['taskId']][0 if stage=='PICKUP' else 1]['code']
                wrong_code = station_code if station_code in ('P1','D1') and station_code != verification['scannedQr'] else 'WRONG'
                rejected = call(f"/api/v1/missions/{mid}/verifications", {**verification, "scannedQr": wrong_code}, expected=(422,))
                assert not rejected["accepted"]
                wrong_checked = True
                verification["requestId"] = str(uuid.uuid4())
            result = call(f"/api/v1/missions/{mid}/verifications", verification)
            assert result["accepted"]
            confirmed.add((mid, stage))
    if len(missions) == len(tasks) and all(m["missionStatus"] == "COMPLETED" for m in missions):
        break
    time.sleep(1)
else:
    raise RuntimeError("Mission timeout: " + str(states))
for code in {m['robotCode'] for m in missions}:
    positions = [(p["x"], p["y"]) for p in poses if p["robotCode"] == code]
    assert positions and max(p[0] for p in positions)-min(p[0] for p in positions) + max(p[1] for p in positions)-min(p[1] for p in positions) > .5, "No measured movement: " + code
assert len(confirmed) == len(tasks)*2
result = {"tasks": tasks, "events": events, "poseSamples": len(poses), "verificationStages": len(confirmed), "wrongQrRejected": wrong_checked,
    "liveCodeCutoverChecked": cutover_checked, "usedFrozenMissionCodes": True,
    "robots": call("/api/v1/robots"), "finalMissions": missions}
output = Path(os.environ.get("SMARTFLEET_EVIDENCE_DIR", root / "test-results/simulation"))
output.mkdir(parents=True, exist_ok=True)
(output / "mission-check.json").write_text(json.dumps(result, indent=2))
print(f"PASS: {len(tasks)} real Webots missions completed; measured movement and {len(confirmed)} verifications persisted.", flush=True)
