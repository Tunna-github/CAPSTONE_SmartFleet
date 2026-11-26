# Shared Webots simulation and live map

Run from the repository root on Windows (Docker Desktop with Linux containers) or a Linux Docker host:

```sh
python tools/setup_demo.py
docker compose up -d --build
```

Open http://localhost:8080, sign in, and select **Simulation / Live Map** in the sidebar. **Fleet map 2D** shows measured positions, headings, planned mission routes, task state, connection and battery. **Connect 3D** opens the live Webots scene. You can pan/zoom the 3D camera; browser viewers cannot reset, pause or drive the simulation. Use the application's task and robot safety controls for authorized commands.

Usernames are `admin`, `operator`, `maintenance`. Each installation generates separate random passwords in its ignored `.env` (`DEMO_ADMIN_PASSWORD`, `DEMO_OPERATOR_PASSWORD`, `DEMO_MAINTENANCE_PASSWORD`). Do not commit or share the whole file. The demo uses its own PostgreSQL volume, MQTT network and two `SIMULATION` robots. It does not connect to the private Aiven database or the native broker on port 1883. Battery is a fixed Webots test value, not a physical battery measurement.

## Another machine views the world running on your machine

On the simulation host:

```sh
python tools/setup_demo.py --lan
docker compose up -d web
```

Find the host's LAN IPv4 address (`ipconfig` on Windows). On another computer on the same network, visit `http://HOST_IP:8080`. That computer only needs a modern WebGL browser and an application account. The host must remain running. Allow inbound TCP 8080 for the intended local network in Windows Firewall if Docker Desktop is not already allowed. Do not enter `localhost` on the remote computer: that refers to the remote computer itself.

The browser receives the same running scene through the frontend/backend gateway. Physics, controllers, MQTT and database stay on the host. A new browser does not create another simulator. Two separate `docker compose up` installations have independent worlds and databases; `git pull` synchronizes source/assets, not live simulation or database state.

For internet deployment, run one Compose stack on a Linux server, put a TLS reverse proxy in front of the **web** service, and use a public DNS name. The proxy must support WebSocket upgrades for `/hubs/robots` and `/api/v1/simulation/view/`, with long read timeouts. Keep database, broker and Webots ports private. Do not scale the backend horizontally: the current navigation coordinator has single-process intersection ownership. Use actual measured host resource usage to choose the server; software rendering of two cameras consumes CPU.

Set `SIMULATION_SECURE_COOKIES=true` in the server's private `.env` when TLS is enabled, and redirect all public HTTP requests to HTTPS. Recreate the backend after changing environment variables. For local HTTP testing the default is false.

## Files and map consistency

* `simulation/worlds/smartfleet.wbt` and its ten textures are portable and use relative paths. Official robot/arena PROTO dependencies and browser modules are pinned to Webots R2025a; initial builds/viewing require network access to the official registries/CDN/GitHub. The image build preloads 77 checksum-verified PROTO/mesh/texture dependencies into the Webots cache, avoiding R2025a Qt HTTP/2 failures during startup. Their source URLs and checksums are in `webots-assets.json`.
* `simulation/config/map.json` is the commissioned grid seeded into the fresh demo database. Subsequent map saves use PostgreSQL. Compose does not overwrite saved maps on restart.
* `scene-manifest.json` records world/texture hashes, the fitted world-to-grid transform and marker installation geometry. The observer registers a fresh run using a private bridge key. Map revision, source, frame, sequence, bounds and timestamp are validated before broadcasting positions.
* World geometry is not regenerated when a user edits the grid. If marker IDs, positions, yaw or sizes differ from the commissioned scene, simulation dispatch is blocked until the world and manifest are recommissioned. Map edits are blocked during active assignments.
* The Supervisor observer supplies **visualization only**. Its position cannot complete a waypoint, confirm a transfer, release an intersection or make a robot available. ArUco camera + IMU, backend grants and FE07 confirmations remain authoritative.
* Camera heading candidates are checked for projection error, floor orientation and ambiguity. A rejected checkpoint frame keeps the robot stopped and does not send waypoint progress or an intersection EXIT; stopped retries are bounded to five seconds. IPPE and SQPnP candidates share the same validation bounds, including on the pinned Docker OpenCV version.
* Gray markers mean stale position. Yellow markers mean only a camera-confirmed QR is known. The frontend does not invent motion between checkpoints.
* Routing excludes another robot's camera-confirmed QR and claimed corridor endpoints, including idle/offline robots. Losing heartbeat does not prove a road is physically clear. Runtime navigation also holds a robot before a destination claimed by another robot; a new camera checkpoint releases the previous corridor. These guards do not replace physical obstacle sensing.
* If Webots stops advancing, the viewer refuses new sessions without fresh pose reports and reports connection timeouts instead of loading forever. Check `docker compose logs --tail 100 webots backend`. A simulator restart resets the simulated scene to its saved starting positions; it does not resolve failed missions or confirm that their loads were recovered.

## Operation and validation

```sh
docker compose ps -a
docker compose logs --tail 100 webots backend
python tools/validate_simulation.py
```

The first start creates an empty schema plus additive migrations and seeds only demo accounts, stations, robots and map. `db-init` exits successfully after initialization; that is expected. Repeated startup preserves tasks and records. Existing non-demo databases are refused. Back up the database volume and `.env` together before changing hosts. Never use `docker compose down -v` on data you want to keep; normal `docker compose down` preserves it.

Backend navigation locks and per-robot safety latches also have named volumes. A restart during an active mission requires recovery; restarting containers is not a way to clear an occupied intersection. The simulation container runs as an unprivileged user. Software-rendering/minimum-GPU warnings can still appear on Docker Desktop; they do not prove a functional failure.

To run the end-to-end check, first finish all active demo assignments; it creates two AUTO tasks and performs simulated operator transfer confirmations:

```sh
python tools/check_simulation_mission.py
python tools/check_simulation_api.py
cd "FrontEnd for SmartFleet"
npm ci
cd ..
node tools/check_simulation_browser.mjs
```

Browser checks use Edge and write screenshots under ignored `test-results/simulation`. The normal frontend/backend/Python regression suites remain separate. A container build and a second browser session on one host do not constitute validation on every operating system or on physical JetRacer hardware.

For the GUI flow on a fresh installation, create an AUTO task using Warehouse 1, Pickup Station 1/P1, Delivery Station 2/D1 and a 1 kg payload. The seeded database mappings resolve P1 to QR2 and D1 to QR7; the task form has no independent marker selectors. Existing installations must commission mappings as Admin in **Warehouse & Stations** first. In **QR Mission Monitor**, confirm pickup using station payload `P1`, then delivery using `D1`. Those confirmations represent simulated handling in this demo. The mission check uses available commissioned station pairs (one pair on a fresh seed). `python tools/check_simulation_api.py --map-write-check` explicitly tests save/restore and blocked dispatch for mismatched marker geometry; it requires no active assignments and cancels its own unassigned probe task.

On a fresh isolated world, first commission a PICKUP station at QR2 and a DELIVERY station at QR5 as Admin (the seeded D1 is at QR7, so explicitly edit it or add a distinct station). Then `python tools/check_simulation_occupancy.py` reproduces the parked-robot regression: R02 stays at QR4 while R01 collects at QR2 and delivers at QR5 using a route around QR4. It creates one MANUAL task and confirms both simulated transfers. Set `SMARTFLEET_DEMO_URL` to select a separate test stack; do not run it on an installation with active user assignments. The script never overrides database station mappings.
