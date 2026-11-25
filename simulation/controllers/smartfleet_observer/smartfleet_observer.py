"""Simulation visualization only. Never supplies navigation or mission evidence."""
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import time
import urllib.request
import uuid

import paho.mqtt.client as mqtt
from controller import Supervisor

root = Path(__file__).resolve().parents[2]
manifest = json.loads((root / "config/scene-manifest.json").read_text(encoding="utf-8"))
world_hash = hashlib.sha256((root / manifest["world"]).read_bytes()).hexdigest()
if world_hash != manifest["worldSha256"]:
    raise RuntimeError("World differs from commissioned simulation manifest")
backend = os.environ.get("SMARTFLEET_BACKEND_URL", "http://localhost:5122").rstrip("/")
bridge_key = os.environ.get("SMARTFLEET_BRIDGE_KEY", "")
if len(bridge_key) < 32:
    raise RuntimeError("SMARTFLEET_BRIDGE_KEY must be configured")

supervisor = Supervisor()
robots = [(r["code"], supervisor.getFromDef(r["defName"])) for r in manifest["robots"]]
if any(node is None for _, node in robots):
    raise RuntimeError("Commissioned robot DEF missing")
client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id="smartfleet-observer-" + uuid.uuid4().hex)
client.connect(os.environ.get("SMARTFLEET_MQTT_HOST", "localhost"),
               int(os.environ.get("SMARTFLEET_MQTT_PORT", "1883")), 30)
client.loop_start()
run_id = str(uuid.uuid4())
revision = None
registration_at = 0.0
published_at = 0.0
sequence = 0
step = int(supervisor.getBasicTimeStep())
print("[OBSERVER] Visualization only; registering scene", flush=True)
try:
    while supervisor.step(step) != -1:
        now = time.monotonic()
        if now - registration_at > 3:
            registration_at = now
            request = urllib.request.Request(backend + "/api/v1/simulation/bridge/register",
                data=json.dumps({"runId": run_id, "worldSha256": world_hash}).encode(),
                headers={"Content-Type": "application/json", "X-Simulation-Key": bridge_key}, method="POST")
            try:
                with urllib.request.urlopen(request, timeout=2) as response:
                    registered = json.load(response)
                if revision != registered["mapRevision"]:
                    print("[OBSERVER] Scene registered; map revision", registered["mapRevision"], flush=True)
                revision = registered["mapRevision"]
            except Exception as ex:
                revision = None
                print("[OBSERVER] Registration unavailable:", type(ex).__name__, flush=True)
        if revision is None or now - published_at < 0.1:
            continue
        published_at = now
        sequence += 1
        for code, node in robots:
            x, y, _ = node.getPosition()
            matrix = node.getOrientation()
            affine = manifest["worldToGrid"]
            yaw = math.degrees(math.atan2(matrix[3], matrix[0])) + manifest["worldToMapYawDeg"]
            pose = {"robotCode": code, "runId": run_id, "sequence": sequence,
                "mapRevision": revision, "source": "SIMULATION_GROUND_TRUTH", "frame": "GRID_COL_ROW",
                "x": affine[0][0]*x + affine[0][1]*y + affine[0][2],
                "y": affine[1][0]*x + affine[1][1]*y + affine[1][2], "yaw": (yaw + 180) % 360 - 180,
                "capturedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat()}
            client.publish("smartfleet/robot/" + code + "/pose", json.dumps(pose), qos=0, retain=False)
finally:
    client.disconnect()
    client.loop_stop()
