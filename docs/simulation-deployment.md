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
* Compose uses a single-thread LLVMpipe renderer with 128-bit vectors and bounded OpenCV/BLAS threads. During regression the default renderer reproducibly stopped all simulation steps on QR8 → QR7; limiting it to two threads alone did not resolve that case. The single-thread/128-bit run completed the problematic leg. Heartbeat and mission safety timeouts remain enforced, and failed load holds are preserved. This is a software-renderer workaround, not a hardware certification. `SIMULATION_RENDER_THREADS` can override the default `0`; validate stability before enabling parallel rendering. See the [Mesa environment settings](https://docs.mesa3d.org/envvars.html) and [LLVMpipe vector settings](https://docs.mesa3d.org/drivers/llvmpipe.html).
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

For the GUI flow on a fresh installation, create an AUTO task using Warehouse 1, Pickup Station 1/P1, Delivery Station 2/D1 and a 1 kg payload. Both task selectors list QR0–QR8; QR1 is disabled as a goods-transfer stop. All eight remaining points support pickup and delivery via explicit TRANSFER stations. P1/QR2 and D1/QR7 remain a smoke-test example, not a task restriction. Older unmodified SIM demo databases receive the same mappings through the one-time `deploy/commission-demo-stations.sql` upgrade. Operators do not need an Admin login to create each task. Customized warehouses still require explicit commissioning once by an Administrator. In **Live tracking & handover**, confirm using the code saved for that mission: new assignments at QR2/QR7 use `QR2`/`QR7`; missions assigned before the code upgrade retain their original `P1`/`D1` codes. Those confirmations represent simulated handling in this demo. The mission check uses available commissioned station pairs (one pair on a fresh seed). `python tools/check_simulation_api.py --map-write-check` explicitly tests save/restore and blocked dispatch for mismatched marker geometry; it requires no active assignments and cancels its own unassigned probe task.

On a fresh isolated world, QR2 and QR5 are already commissioned transfer points. Then `python tools/check_simulation_occupancy.py` reproduces the parked-robot regression: R02 stays at QR4 while R01 collects at QR2 and delivers at QR5 using a route around QR4. It creates one MANUAL task and confirms both simulated transfers. Set `SMARTFLEET_DEMO_URL` to select a separate test stack; do not run it on an installation with active user assignments. The script never overrides database station mappings.


## Ready-to-use stations on older demo volumes

`db-init` now upgrades the original SIM preset as well as installing fresh databases. It identifies P1/D1 by station codes/types and links them to QR2/QR7 without assuming their numeric StationIDs. The upgrade records system audit entries and an applied version, and never replaces existing mappings, map geometry, robot state, tasks or mission history. It only runs for recognized demo databases whose physical QR geometry and corridor topology match the shipped scene; customized/occupied stations, marker conflicts and active assignments/failed holds defer it. It does not infer mappings for a real warehouse.

To apply additive updates to an older demo volume after pulling:

```sh
docker compose run --rm --no-deps db-init
```

The normal `docker compose up` also runs `db-init`. Operators can then reload stations in Create Task without switching accounts. The task form shows unavailable pickup/delivery stations with reasons instead of silently hiding them, and enables creation after both stations are selected and ready. WAITING and CHARGING remain separate from goods-transfer stations.

Regression commands, using a separate disposable test installation:

```sh
docker compose -p smartfleet-station-flow up -d database
docker compose -p smartfleet-station-flow run --rm --no-deps db-init
python tools/check_demo_station_upgrade.py --project smartfleet-station-flow
```

The upgrade check creates/removes database clones and tests fresh/idempotent initialization, legacy StationID changes, deliberate customization, changed QR geometry/corridors, conflicting markers, active failed holds and non-demo databases. `node tools/check_operator_station_flow.mjs http://localhost:8080` checks the main form without creating a task. Adding `--create` is restricted to the isolated acceptance installation on port 8086 and exercises real MANUAL/AUTO task creation with an Operator-only login; robots there remain offline so both tasks stay queued.

The subsequent SIM preset v3 upgrade (`deploy/commission-demo-qr-stations.sql`) commissions all eight non-intersection QR points as TRANSFER stations. It extends the earlier two-point legacy repair. See [QR endpoint scope and model](warehouse-station-management.md#scope-clarification-any-eligible-qr-can-be-a-task-endpoint). Run the PostgreSQL upgrade suite with `--qr-transfer` to include dedicated-berth and missing-verification safeguards.

Preset v5 unifies the verification payloads at QR2/QR7. It preserves existing mission snapshots and historical scans; the UI displays the frozen mission code. See [verification-code cutover semantics](warehouse-station-management.md#unified-verification-codes-sim-preset-v5). Validate with `python tools/check_verification_code_upgrade.py --project smartfleet-station-qr-flow` against an isolated stopped test database.
