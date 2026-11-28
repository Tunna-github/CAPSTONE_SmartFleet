# Docker MQTT and an existing JetRacer notebook

The existing broker stays in Docker. Backend connections remain `mqtt:1883`.
The optional `compose.mqtt-hotspot.yaml` publishes the broker's TCP 1883 on the
Windows hotspot address, normally `192.168.137.1:1883`. A JetRacer such as
`192.168.137.168` connects to that address using its existing paho-mqtt code.
The same established TCP connection carries commands, status, telemetry and ACKs
in both directions. No second Windows/JetRacer broker or MQTT bridge is required.

## Enable once

Turn on Mobile hotspot, connect the JetRacer and open Docker Desktop. In the repo:

```powershell
python tools/mqtt_hotspot.py --enable
```

This backs up the private `.env` outside Git, retains the selected Aiven/map
overrides, adds the optional port binding and starts **only mqtt**. It refuses to
recreate an unpublished broker while backend/Webots is running, and refuses a
port occupied by another listener. It never kills a port owner, changes robot
identity, modifies notebooks or changes the database.

Later starts use the saved binding automatically; for the broker alone:

```powershell
docker compose up -d mqtt
docker compose ps mqtt
```

Expect `192.168.137.1:1883->1883/tcp`. The hotspot must be on before Docker binds
this address. Override with `--address`/`--port` only if your network differs.
Re-running Aiven configuration or charging-layout setup preserves this opt-in.

Keep the existing robot workflow: start the ROS robot, camera, LiDAR and
web-video-server commands, then run the existing notebooks. No OpenCV, ROS or
paho-mqtt installation is needed. The repository's `Robot code` directory remains
reference material; this network change does not deploy it onto the robot.

## Firewall, only if an actual connection is blocked

No firewall change was needed in the verified setup. A narrow optional rule can
be installed once from an Administrator PowerShell:

```powershell
.\tools\mqtt_hotspot_firewall.ps1 -Apply -RobotAddress 192.168.137.168
```

The rule allows inbound TCP to the selected hotspot IP/port from the listed robot
IPs. It leaves Windows Firewall enabled and does not install a Windows broker.
Update the list when DHCP assigns the robot a different address. The broker is
bound to the hotspot address, not all host interfaces; this retains the existing
anonymous MQTT setup for the trusted local robot network.

## Diagnostic test without driving

With a trusted SSH host key and the robot's existing Python/paho installation:

```powershell
python tools/check_mqtt_hotspot.py --robot-host 192.168.137.168 --ssh-user jetson
```

The checking PC needs `paramiko`; the tool prompts for the SSH password and does
not store it. `--known-hosts PATH` can select an existing trusted host-key file.
It runs a temporary diagnostic subscriber in a separate process, publishes a
unique nonce from the Docker broker and verifies the exact reply from JetRacer.
Only `smartfleet/diagnostics/hotspot/...` topics are used; no motor, task, robot
status or notebook code is executed, and no robot file/package is installed.

On 2026-11-28, the actual JetRacer `192.168.137.168` connected to the published
Docker broker and passed both directions. Existing notebooks, ROS commands and
robot code R01 were retained; firewall changes were unnecessary.

Do not run physical R01 and Webots R01 controllers at the same time on this
broker: both would receive the same command topic. Port publishing does not
change registration, dispatch eligibility or firmware capabilities. This setup
intentionally does not start backend/Webots or send a task to a physical robot.
