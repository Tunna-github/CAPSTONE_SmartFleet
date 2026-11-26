"""FE-02 integration acceptance. Use an isolated Compose database, never operational data.

Requires explicit --url. Writes catalog items, creates one task and confirms its
simulated transfers. Requires fresh demo stations P1/D1 and no active assignments.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--url", required=True)
parser.add_argument("--api-only", action="store_true", help="Run additional constraint/concurrency checks on an already exercised isolated installation")
args = parser.parse_args()
base = args.url.rstrip("/")
root = Path(__file__).resolve().parents[1]
env = dict(s.split("=", 1) for s in (root / ".env").read_text().splitlines() if "=" in s)
evidence = []


def call(path, token=None, body=None, method=None, expected=(200,)):
    request = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), method=method,
        headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + token} if token else {})})
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            status, raw = response.status, response.read()
    except urllib.error.HTTPError as error:
        status, raw = error.code, error.read()
    assert status in expected, (path, status, raw.decode()[:800])
    return json.loads(raw) if raw else None


tokens = {name: call("/api/v1/auth/login", body={"usernameOrEmail": name, "password": env[key]})["accessToken"]
    for name, key in [("admin", "DEMO_ADMIN_PASSWORD"), ("operator", "DEMO_OPERATOR_PASSWORD"), ("maintenance", "DEMO_MAINTENANCE_PASSWORD")]}
admin, operator, maintenance = (tokens[n] for n in ("admin", "operator", "maintenance"))
assert not any(m["assignmentStatus"] == "ACTIVE" for m in call("/api/v1/missions", operator)), "Use an isolated test installation with no active assignments"
if args.api_only:
    catalog = call('/api/v1/warehouse-catalog', admin)
    p = next(s for s in catalog['stations'] if s['code']=='P1')
    def create(code):
        body = {k:p[k] for k in ['zoneId','coordX','coordY','coordZ','yawAngle']}
        body.update(code=code,name=code,type='WAITING',qrCodePayload=None,arucoId=6,isActive=True,capacity=1,revision=0)
        return call('/api/v1/warehouse-catalog/stations',admin,body,expected=(200,409))
    suffix = uuid.uuid4().hex[:8]
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(create,['RACEA-'+suffix,'RACEB-'+suffix]))
    assert sum('id' in r for r in results)==1, results
    winner = next(r['id'] for r in results if 'id' in r)
    station = next(s for s in call('/api/v1/warehouse-catalog',admin)['stations'] if s['stationId']==winner)
    body={k:station[k] for k in ['zoneId','code','name','type','coordX','coordY','coordZ','yawAngle','qrCodePayload','arucoId','isActive','capacity','revision']}
    call(f'/api/v1/warehouse-catalog/stations/{winner}',admin,{**body,'arucoId':None,'isActive':False},'PUT')
    inactive=next(s for s in call('/api/v1/warehouse-catalog',admin)['stations'] if s['stationId']==winner)
    assert not inactive['isActive'] and inactive['arucoId'] is None
    print('PASS: concurrent same-marker writes accept exactly one; deactivate/unmap persists.',flush=True)
    raise SystemExit(0)
for token in tokens.values():
    assert len(call("/api/v1/warehouse-catalog", token)["stations"]) >= 2
call("/api/v1/warehouse-catalog", expected=(401,))
call("/api/v1/warehouse-catalog/warehouses", operator, {"code": "DENIED", "name": "Denied", "isActive": True, "revision": 0}, expected=(403,))
call("/api/v1/warehouse-catalog/zones", maintenance, {"warehouseId": 1, "code": "DENIED", "name": "Denied", "type": "TRANSIT", "speedLimit": .2, "isActive": True, "revision": 0}, expected=(403,))
saved = call("/api/v1/navigation/map", admin)
scene = call("/api/v1/simulation", operator)


def station_body(station, **changes):
    names = ["zoneId", "code", "name", "type", "coordX", "coordY", "coordZ", "yawAngle", "qrCodePayload", "arucoId", "isActive", "capacity", "revision"]
    return {**{k: station[k] for k in names}, **changes}


catalog = call("/api/v1/warehouse-catalog", admin)
pickup = next(s for s in catalog["stations"] if s["code"] == "P1")
delivery = next(s for s in catalog["stations"] if s["code"] == "D1")
call(f'/api/v1/warehouse-catalog/stations/{pickup["stationId"]}', admin, station_body(pickup, arucoId=2), "PUT")
call(f'/api/v1/warehouse-catalog/stations/{delivery["stationId"]}', admin, station_body(delivery, arucoId=7), "PUT")
catalog = call("/api/v1/warehouse-catalog", admin)
pickup = next(s for s in catalog["stations"] if s["code"] == "P1")
delivery = next(s for s in catalog["stations"] if s["code"] == "D1")
assert (pickup["arucoId"], delivery["arucoId"]) == (2, 7)
assert saved == call("/api/v1/navigation/map", admin), "Station commissioning must not move physical map markers"
assert call("/api/v1/simulation", operator)["mapCompatible"] == scene["mapCompatible"]
for qr, status in [(2, 409), (1, 400), (49, 400)]:
    call(f'/api/v1/warehouse-catalog/stations/{delivery["stationId"]}', admin, station_body(delivery, arucoId=qr), "PUT", expected=(status,))
call(f'/api/v1/warehouse-catalog/stations/{delivery["stationId"]}', operator, station_body(delivery), "PUT", expected=(403,))
call(f'/api/v1/warehouse-catalog/stations/{delivery["stationId"]}', maintenance, station_body(delivery), "PUT", expected=(403,))
call(f'/api/v1/warehouse-catalog/stations/{delivery["stationId"]}', admin, station_body(delivery, revision=0), "PUT", expected=(409,))

# An operational station cannot become an intersection or lose its QR mapping.
invalid = json.loads(json.dumps(saved))
next(c for row in invalid["cells"] for c in row if c.get("arucoId") == 2)["isIntersection"] = True
call("/api/v1/navigation/map", admin, invalid, "PUT", expected=(409,))
assert saved == call("/api/v1/navigation/map", admin)

# Catalog lifecycle and one-berth policy, without pretending a reservation docks a robot.
warehouse_id = call("/api/v1/warehouse-catalog/warehouses", admin, {"code": "FE02", "name": "FE02 test", "address": "Test only", "isActive": True, "revision": 0})["id"]
zone_id = call("/api/v1/warehouse-catalog/zones", admin, {"warehouseId": warehouse_id, "code": "TRANSIT", "name": "Transit", "type": "TRANSIT", "speedLimit": .2, "isActive": True, "revision": 0})["id"]
call(f'/api/v1/warehouse-catalog/warehouses/{warehouse_id}', admin, {"code": "FE02", "name": "FE02 test", "address": "", "isActive": False, "revision": 1}, "PUT", expected=(409,))
waiting = station_body(pickup, code="WAIT-TEST", name="Waiting berth", type="WAITING", qrCodePayload=None, arucoId=8, revision=0)
waiting_id = call("/api/v1/warehouse-catalog/stations", admin, waiting)["id"]
call(f'/api/v1/warehouse-catalog/stations/{waiting_id}/berth', operator, {"robotId": 2, "release": False, "reason": "Denied reservation"}, expected=(403,))
call(f'/api/v1/warehouse-catalog/stations/{waiting_id}/berth', maintenance, {"robotId": 2, "release": False, "reason": "Reserve test berth"})
call(f'/api/v1/warehouse-catalog/stations/{waiting_id}/berth', maintenance, {"robotId": 1, "release": False, "reason": "Capacity test"}, expected=(409,))
call(f'/api/v1/warehouse-catalog/stations/{waiting_id}/berth', maintenance, {"robotId": 2, "release": True, "physicalAreaConfirmedClear": False, "reason": "No clearance"}, expected=(409,))
call(f'/api/v1/warehouse-catalog/stations/{waiting_id}/berth', maintenance, {"robotId": 2, "release": True, "physicalAreaConfirmedClear": True, "reason": "Physically confirmed clear"})
task_body = {"warehouseId": 1, "pickupStationId": pickup["stationId"], "deliveryStationId": delivery["stationId"], "payloadWeightKg": 1,
    "priorityLevel": 2, "dispatchMode": "MANUAL", "itemDescription": "FE02 station-route acceptance"}
call("/api/v1/transport-tasks", operator, {**task_body, "pickupArUcoId": 0, "deliveryArUcoId": 7}, expected=(400,))
call("/api/v1/transport-tasks", operator, {**task_body, "pickupStationId": waiting_id}, expected=(400,))
for _ in range(120):
    if len(call("/api/v1/demo-routing/robots/positions", operator)) == 2:
        break
    time.sleep(1)
else:
    raise RuntimeError("Both Webots robots must localize")
task = call("/api/v1/transport-tasks", operator, task_body, expected=(201,))
robots = call(f'/api/v1/transport-tasks/{task["taskId"]}/available-robots?includeIneligible=true', operator)
robot = next(r for r in robots if r["robotCode"] == "R01")
assert robot["isEligible"], robot
call(f'/api/v1/transport-tasks/{task["taskId"]}/assign', operator, {"robotId": robot["robotId"]})
call(f'/api/v1/warehouse-catalog/stations/{pickup["stationId"]}', admin, station_body(pickup, name="Unsafe live edit"), "PUT", expected=(409,))
states, confirmed = [], set()
deadline = time.monotonic() + 600
while time.monotonic() < deadline:
    mission = next(m for m in call("/api/v1/missions", operator) if m["taskId"] == task["taskId"])
    assert (mission["pickupArUcoId"], mission["deliveryArUcoId"]) == (2, 7)
    if not states or states[-1] != mission["missionStatus"]:
        states.append(mission["missionStatus"]); print("Mission", mission["missionId"], states[-1], flush=True)
    assert mission["missionStatus"] not in ("FAILED", "CANCELLED"), mission
    stage = "PICKUP" if mission["missionStatus"] == "AT_PICKUP" else "DELIVERY" if mission["missionStatus"] == "AT_DELIVERY" else None
    if stage and stage not in confirmed and mission["stationPresenceFresh"]:
        result = call(f'/api/v1/missions/{mission["missionId"]}/verifications', operator, {"requestId": str(uuid.uuid4()), "stage": stage,
            "type": "QRCODE", "scannedQr": pickup["qrCodePayload"] if stage == "PICKUP" else delivery["qrCodePayload"], "physicalTransferConfirmed": True})
        assert result["accepted"]; confirmed.add(stage)
    if mission["missionStatus"] == "COMPLETED": break
    time.sleep(1)
else: raise RuntimeError("Mission timeout: " + str(states))
assert confirmed == {"PICKUP", "DELIVERY"}
assert saved == call("/api/v1/navigation/map", admin)
output = Path(os.environ.get("SMARTFLEET_EVIDENCE_DIR", root / "test-results/fe02")); output.mkdir(parents=True, exist_ok=True)
(output / "station-acceptance.json").write_text(json.dumps({"taskId": task["taskId"], "states": states,
    "pickup": {"stationId": pickup["stationId"], "arucoId": 2}, "delivery": {"stationId": delivery["stationId"], "arucoId": 7},
    "mapUnchanged": True, "invalidMappingsRejected": True, "authorizationChecked": True, "berthCapacityChecked": True,
    "activeMissionEditRejected": True, "finalMission": mission}, indent=2))
print("PASS: P1 -> QR2 / D1 -> QR7; station-only task completed with persisted verification. Invalid mappings/roles/capacity/live edits rejected.", flush=True)
