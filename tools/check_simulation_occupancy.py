"""Regression: R02 parked at QR4 must not obstruct R01's QR2 -> QR5 delivery.

Run against a fresh isolated Compose installation with no active assignments.
Creates one MANUAL task and performs simulated FE07 transfer confirmations.
"""
import json
import os
from pathlib import Path
import time
import urllib.request
import uuid

root = Path(__file__).resolve().parents[1]
env = dict(line.split("=", 1) for line in (root / ".env").read_text().splitlines() if "=" in line)
base = os.environ.get("SMARTFLEET_DEMO_URL", "http://127.0.0.1:" + env.get("WEB_PORT", "8080"))
token = None


def call(path, data=None):
    request = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(),
        headers={"Content-Type": "application/json", **({"Authorization": "Bearer " + token} if token else {})})
    with urllib.request.urlopen(request, timeout=15) as response:
        return json.load(response)


token = call("/api/v1/auth/login", {"usernameOrEmail": "admin", "password": env["DEMO_ADMIN_PASSWORD"]})["accessToken"]
catalog = call('/api/v1/warehouse-catalog')
pickup = next((s for s in catalog['stations'] if s['arucoId']==2 and s['type'] in ('PICKUP','TRANSFER') and s['usable']), None)
delivery = next((s for s in catalog['stations'] if s['arucoId']==5 and s['type'] in ('DELIVERY','TRANSFER') and s['usable']), None)
assert pickup and delivery, 'Commission active PICKUP QR2 and DELIVERY QR5 stations in Warehouse & Stations before this isolated test'
token = call("/api/v1/auth/login", {"usernameOrEmail": "operator", "password": env["DEMO_OPERATOR_PASSWORD"]})["accessToken"]
assert not any(m["assignmentStatus"] == "ACTIVE" for m in call("/api/v1/missions")), "Finish active missions first"
for _ in range(120):
    positions = call("/api/v1/demo-routing/robots/positions")
    if positions.get("R01") == 0 and positions.get("R02") == 4:
        break
    time.sleep(1)
else:
    raise RuntimeError("This regression requires a fresh world: R01 QR0 and R02 QR4")
task = call("/api/v1/transport-tasks", {"warehouseId": pickup['warehouseId'], "pickupStationId": pickup['stationId'], "deliveryStationId": delivery['stationId'],
    "payloadWeightKg": 1, "priorityLevel": 2, "dispatchMode": "MANUAL", "itemDescription": "Parked QR4 robot regression"})
available = call(f'/api/v1/transport-tasks/{task["taskId"]}/available-robots?includeIneligible=true')
selected = next(r for r in available if r["robotCode"] == "R01")
assert selected["isEligible"], selected.get("ineligibilityReason")
call(f'/api/v1/transport-tasks/{task["taskId"]}/assign', {"robotId": selected["robotId"]})
confirmed = set()
states = []
deadline = time.monotonic() + 600
route = None
while time.monotonic() < deadline:
    mission = next(m for m in call("/api/v1/missions") if m["taskId"] == task["taskId"])
    route = [w["arucoId"] for w in mission["waypoints"]]
    assert 4 not in route, f"Route must avoid parked R02 at QR4: {route}"
    if not states or states[-1] != mission["missionStatus"]:
        states.append(mission["missionStatus"])
        print("R01", states[-1], "route", route, flush=True)
    assert mission["missionStatus"] not in ("FAILED", "CANCELLED"), mission
    stage = "PICKUP" if mission["missionStatus"] == "AT_PICKUP" else "DELIVERY" if mission["missionStatus"] == "AT_DELIVERY" else None
    if stage and stage not in confirmed and mission["stationPresenceFresh"]:
        result = call(f'/api/v1/missions/{mission["missionId"]}/verifications', {
            "requestId": str(uuid.uuid4()), "stage": stage, "type": "QRCODE",
            "scannedQr": mission['pickupVerificationCode'] if stage == "PICKUP" else mission['deliveryVerificationCode'], "physicalTransferConfirmed": True})
        assert result["accepted"]
        confirmed.add(stage)
    if mission["missionStatus"] == "COMPLETED":
        break
    time.sleep(1)
else:
    raise RuntimeError("Mission did not complete: " + str(states))
assert confirmed == {"PICKUP", "DELIVERY"}
assert call("/api/v1/demo-routing/robots/positions").get("R02") == 4
assert call("/api/v1/simulation")["streamReady"]
output = Path(os.environ.get("SMARTFLEET_EVIDENCE_DIR", root / "test-results/simulation"))
output.mkdir(parents=True, exist_ok=True)
(output / "parked-robot.json").write_text(json.dumps({"taskId": task["taskId"], "route": route,
    "states": states, "confirmations": sorted(confirmed), "parkedRobot": "R02", "parkedQr": 4}, indent=2))
print("PASS: R01 delivered around parked R02; both confirmations persisted and simulator remains responsive.", flush=True)
