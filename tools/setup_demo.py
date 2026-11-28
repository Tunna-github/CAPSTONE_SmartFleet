"""Generate private credentials once; keep the resulting .env out of Git."""
import argparse
from pathlib import Path
import secrets
import os
from aiven_support import read_env
from mqtt_hotspot import keep_hotspot_overlay

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, default=8080)
parser.add_argument("--lan", action="store_true", help="Listen on all host interfaces so LAN browsers can connect")
parser.add_argument("--charging-layout", action="store_true", help="Select the commissioned separated-lane/four-bay layout for subsequent Docker Compose commands")
args = parser.parse_args()
destination = root / ".env"
if destination.exists():
    if args.lan:
        lines=destination.read_text(encoding="utf-8").splitlines()
        lines=[line for line in lines if not line.startswith("BIND_ADDRESS=")]+["BIND_ADDRESS=0.0.0.0"]
        destination.write_text("\n".join(lines)+"\n",encoding="utf-8")
    print("Keeping existing", destination, "(credentials must match the database volume)")
else:
    values = {name: secrets.token_hex(32) for name in ["POSTGRES_PASSWORD", "JWT_KEY", "SIMULATION_BRIDGE_KEY",
        "DEMO_ADMIN_PASSWORD", "DEMO_OPERATOR_PASSWORD", "DEMO_MAINTENANCE_PASSWORD"]}
    values.update(WEB_PORT=str(args.port), BIND_ADDRESS="0.0.0.0" if args.lan else "127.0.0.1")
    destination.write_text("\n".join(f"{key}={value}" for key, value in values.items()) + "\n", encoding="utf-8")
    print("Created private credentials:", destination)
if args.charging_layout:
    lines=destination.read_text(encoding="utf-8").splitlines()
    shared = any(line.startswith("AIVEN_CONNECTION_STRING=") for line in lines)
    lines=[line for line in lines if not line.startswith("COMPOSE_FILE=")]
    files=["compose.yaml","compose.charging.yaml"]
    if shared: files.append("compose.aiven.yaml")
    files=keep_hotspot_overlay(files,read_env())
    lines.append("COMPOSE_FILE="+os.pathsep.join(files))
    destination.write_text("\n".join(lines)+"\n",encoding="utf-8")
    if shared: print("Preserved shared Aiven configuration. Select map1/map2 with tools/select_aiven_map.py; no database was changed.")
    else: print("Selected separated lanes and four charging bays. Existing active work must be recovered before physical scene commissioning.")
print("Login usernames: admin, operator, maintenance. Passwords are in the private .env file.")
