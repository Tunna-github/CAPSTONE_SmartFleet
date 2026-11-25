"""End-to-end demo check: real controller movement + FE07 operator confirmations.

Creates two AUTO test tasks in the isolated Compose demo database. Never targets Aiven.
"""
import json
import os
import sys
from pathlib import Path
import time
import urllib.error
import urllib.request
import uuid

root = Path(__file__).resolve().parents[1]
env = dict(line.split("=", 1) for line in (root / ".env").read_text().splitlines() if "=" in line)
base = os.environ.get("SMARTFLEET_DEMO_URL", "http://127.0.0.1:" + env.get("WEB_PORT", "8080"))
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
routes = [(4, 7)] if '--manual' in sys.argv else [(0, 2)] if '--single' in sys.argv else [(0, 2), (4, 7)]
for pickup, delivery in routes:
    task = call("/api/v1/transport-tasks", {"warehouseId": 1, "pickupStationId": 1, "deliveryStationId": 2,
        "payloadWeightKg": 1, "priorityLevel": 2, "dispatchMode": "MANUAL" if '--manual' in sys.argv else "AUTO", "pickupArUcoId": pickup,
        "deliveryArUcoId": delivery, "itemDescription": "Compose integration check"})
    tasks.append(task["taskId"])
    if '--manual' in sys.argv:
        available = call(f'/api/v1/transport-tasks/{task["taskId"]}/available-robots?includeIneligible=true')
        selected = next(r for r in available if r['robotCode']=='R02')
        assert selected['isEligible'],selected.get('ineligibilityReason')
        assigned = call(f'/api/v1/transport-tasks/{task["taskId"]}/assign',{'robotId':selected['robotId']})
        print('MANUAL assignment accepted for R02',flush=True)
print("Created demo tasks", tasks, flush=True)
events = []
states = {}
confirmed = set()
wrong_checked = False
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
        assert status not in ("FAILED", "CANCELLED"), f"Mission failed: {mid}"
        stage = "PICKUP" if status == "AT_PICKUP" else "DELIVERY" if status == "AT_DELIVERY" else None
        if stage and (mid, stage) not in confirmed and mission["stationPresenceFresh"]:
            verification = {"requestId": str(uuid.uuid4()), "stage": stage, "type": "QRCODE",
                "scannedQr": "P1" if stage == "PICKUP" else "D1", "scannedPackageCode": None,
                "physicalTransferConfirmed": True, "note": None}
            if not wrong_checked:
                rejected = call(f"/api/v1/missions/{mid}/verifications", {**verification, "scannedQr": "WRONG"}, expected=(422,))
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
    "robots": call("/api/v1/robots"), "finalMissions": missions}
output = Path(os.environ.get("SMARTFLEET_EVIDENCE_DIR", root / "test-results/simulation"))
output.mkdir(parents=True, exist_ok=True)
(output / "mission-check.json").write_text(json.dumps(result, indent=2))
print(f"PASS: {len(tasks)} real Webots missions completed; measured movement and {len(confirmed)} verifications persisted.", flush=True)
