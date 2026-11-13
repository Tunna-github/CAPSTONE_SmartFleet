"""Apply integration points to the CAPSTONE_SmartFleet repo, conservatively.

1. Extract SmartFleet_Aruco_LiveMap.zip INTO the repo root (allow merge folders).
2. From the repo root: python apply_smartfleet_map.py
3. Review .aruco-backup files, build frontend/backend, then test.

The script does not touch database connections, JWT, robot motion or line control.
"""
from pathlib import Path
import re
import sys

ROOT = Path.cwd()


def replace_once(path, old, new, name):
    if old == new:
        return
    contents = path.read_text(encoding="utf-8-sig")
    if new in contents:
        print("[SKIP] {} already applied".format(name))
        return
    count = contents.count(old)
    if count != 1:
        raise RuntimeError("{}: expected one exact insertion point, got {} in {}".format(name, count, path))
    save(path, contents.replace(old, new, 1), name)


def save(path, old_or_new, name, original=None):
    old = path.read_text(encoding="utf-8-sig") if original is None else original
    new = old_or_new
    if new == old:
        return
    backup = path.with_name(path.name + ".aruco-backup")
    if not backup.exists():
        backup.write_text(old, encoding="utf-8")
    path.write_text(new, encoding="utf-8")
    print("[OK] {} -> {}".format(name, path))


def require_file(relative):
    p = ROOT / relative
    if not p.is_file():
        raise FileNotFoundError("Missing file: {}. Extract kit into repository root first.".format(relative))
    return p


def patch_dbcontext():
    p = require_file("SmartFleetBE/Model/SmartFleetDbContext.cs")
    current = p.read_text(encoding="utf-8-sig")
    if "DbSet<ArucoMarkerMap>" in current:
        print("[SKIP] DbContext previously patched")
        return
    x = current.replace(
        "    public virtual DbSet<MapConfiguration> MapConfigurations { get; set; }",
        "    public virtual DbSet<MapConfiguration> MapConfigurations { get; set; }\n"
        "\n"
        "    public virtual DbSet<ArucoMarkerMap> ArucoMarkerMaps { get; set; }\n"
        "\n"
        "    public virtual DbSet<RobotArucoPosition> RobotArucoPositions { get; set; }",
    )
    if x == current:
        raise RuntimeError("DbContext DbSet anchor missing")
    anchor = "        modelBuilder.Entity<RobotCommandOutbox>(entity =>"
    block = '''        // ArUco -> grid cell configuration, independent of continuous ROS coordinates.
        modelBuilder.Entity<ArucoMarkerMap>(entity =>
        {
            entity.ToTable("ArucoMarkerMaps");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.CellCode).HasMaxLength(16);
            entity.HasIndex(e => new { e.WarehouseId, e.ArucoId }).IsUnique();
            entity.HasIndex(e => new { e.WarehouseId, e.GridColumn, e.GridRow }).IsUnique();
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(e => e.WarehouseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RobotArucoPosition>(entity =>
        {
            entity.ToTable("RobotArucoPositions");
            entity.HasKey(e => e.RobotId);
            entity.HasOne<Robot>().WithMany().HasForeignKey(e => e.RobotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

'''
    if x.count(anchor) != 1:
        raise RuntimeError("DbContext OnModelCreating anchor missing")
    x = x.replace(anchor, block + anchor)
    save(p, x, "EF Core map entities")


def patch_program():
    p = require_file("SmartFleetBE/Program.cs")
    old = "    builder.Services.AddHostedService<MqttRobotTelemetryWorker>();"
    new = old + "\n\n    // Independent ArUco marker subscriber for the live warehouse map.\n    builder.Services.AddHostedService<MqttArucoPositionWorker>();"
    replace_once(p, old, new, "Register ArUco MQTT background worker")


def patch_frontend_app():
    p = require_file("FrontEnd for SmartFleet/src/App.tsx")
    s = p.read_text(encoding="utf-8-sig")
    changed = False
    if 'import { WarehouseMapPage }' not in s:
        anchor = 'import { RobotsPage } from "./pages/RobotsPage";'
        if s.count(anchor) != 1:
            raise RuntimeError("App.tsx import anchor missing")
        s = s.replace(anchor, anchor + '\nimport { WarehouseMapPage } from "./pages/WarehouseMapPage";')
        changed = True

    if 'path="/dashboard/map"' not in s:
        anchor = '''                                    <Route
                                        path="/dashboard/robots"
                                        element={<RobotsPage />}
                                    />'''
        extra = '''
                                    <Route
                                        path="/dashboard/map"
                                        element={<WarehouseMapPage />}
                                    />'''
        if s.count(anchor) != 1:
            raise RuntimeError("App.tsx shared robot route anchor missing")
        s = s.replace(anchor, anchor + extra)
        changed = True

    if 'Admin Maps (Coming Soon)' in s:
        old = '''                                    <Route
                                        path="/admin/maps"
                                        element={
                                            <div className="p-6">
                                                Admin Maps (Coming Soon)
                                            </div>
                                        }
                                    />'''
        new = '''                                    <Route
                                        path="/admin/maps"
                                        element={<WarehouseMapPage />}
                                    />'''
        if s.count(old) != 1:
            raise RuntimeError("App.tsx admin maps placeholder anchor missing")
        s = s.replace(old, new)
        changed = True
    if changed:
        save(p, s, "Admin and operator map routes")
    else:
        print("[SKIP] App.tsx previously patched")


def patch_sidebar():
    p = require_file("FrontEnd for SmartFleet/src/components/Sidebar.tsx")
    s = p.read_text(encoding="utf-8-sig")
    if 'label: "Live Map"' in s:
        print("[SKIP] Sidebar map links already present")
        return
    pattern = r'(?m)^([ \t]*)\{ id: "robots", label: "Fleet Status", path: "/dashboard/robots", icon: IC\.robots \},$'
    matches = list(re.finditer(pattern, s))
    if len(matches) != 2:
        raise RuntimeError("Sidebar: expected Fleet Status entries for both maintenance and operator")
    def add(m):
        line = m.group(0)
        return line + '\n' + m.group(1) + '{ id: "map", label: "Live Map", path: "/dashboard/map", icon: IC.maps },'
    updated = re.sub(pattern, add, s)
    save(p, updated, "Operator/maintenance Live Map sidebar navigation")


def patch_robot_main():
    p = require_file("Robot code/main_code.py")
    s = p.read_text(encoding="utf-8-sig")
    changed = False
    if "def report_map_marker_from_ids" not in s:
        anchor = "def wait_for_aruco_id(target_id, timeout=None):"
        if s.count(anchor) != 1:
            raise RuntimeError("main_code.py ArUco scanner anchor missing")
        helper = '''# MQTT map fix: send a marker ID, NEVER guess A1/B2 on the robot.
_last_sent_map_marker = None
_last_sent_map_at = 0.0


def report_map_marker_from_ids(ids):
    global _last_sent_map_marker, _last_sent_map_at
    if not ids or not mqtt_connected.is_set():
        return
    marker_id = int(ids[0])  # This scanner returns sorted IDs.
    now = time.monotonic()
    if marker_id == _last_sent_map_marker and now - _last_sent_map_at < 5.0:
        return
    payload = json.dumps({"arucoId": marker_id})
    topic = "smartfleet/robot/{}/aruco".format(ROBOT_CODE)
    try:
        result = client.publish(topic, payload, qos=1, retain=False)
        if result.rc == mqtt.MQTT_ERR_SUCCESS:
            _last_sent_map_marker = marker_id
            _last_sent_map_at = now
            print("[MAP] ArUco {} -> backend position mapper".format(marker_id), flush=True)
        else:
            print("[MAP] MQTT publish failed rc={}".format(result.rc), flush=True)
    except Exception as exc:
        print("[MAP] Send failed: {}".format(exc), flush=True)


'''
        s = s.replace(anchor, helper + anchor)
        changed = True

    if "report_map_marker_from_ids(ids)" not in s.replace("def report_map_marker_from_ids(ids):", ""):
        pattern = r'(?m)^([ \t]*)ids = detect_aruco_ids_from_frame\(frame\)[ \t]*$'
        if len(re.findall(pattern, s)) != 2:
            raise RuntimeError("main_code.py: expected two marker detection call sites")
        s = re.sub(pattern, lambda m: m.group(0) + '\n' + m.group(1) + 'report_map_marker_from_ids(ids)', s)
        changed = True

    if changed:
        save(p, s, "Report map markers during mission scan and line following")
    else:
        print("[SKIP] Robot main scanner already patched")


def patch_qr_scanner():
    p = require_file("Robot code/qr_scanning_code.py")
    s = p.read_text(encoding="utf-8-sig")
    changed = False
    if "import paho.mqtt.client as mqtt" not in s:
        if "import json\n" not in s:
            raise RuntimeError("qr_scanning_code.py import anchor missing")
        s = s.replace("import json\n", "import json\nimport uuid\nimport paho.mqtt.client as mqtt\n", 1)
        changed = True

    if "def report_qr_map_markers" not in s:
        anchor = 'status_view = display(HTML("<pre>Dang chuan bi doc /stream...</pre>"), display_id=True)'
        if s.count(anchor) != 1:
            raise RuntimeError("qr_scanning_code.py status view anchor missing")
        helper = '''# Map output uses a separate MQTT client; circle movement logic is unchanged.
MAP_BROKER_IP = "192.168.137.1"
MAP_BROKER_PORT = 1883
MAP_ROBOT_CODE = "R01"
_map_qr_client = None
_last_qr_map_id = None
_last_qr_map_at = 0.0
try:
    _map_qr_client = mqtt.Client(
        client_id="smartfleet-qrmap-{}-{}".format(MAP_ROBOT_CODE.lower(), uuid.uuid4().hex[:8]))
    _map_qr_client.connect(MAP_BROKER_IP, MAP_BROKER_PORT, 60)
    _map_qr_client.loop_start()
    print("[MAP] MQTT map publisher started", flush=True)
except Exception as exc:
    print("[MAP] MQTT unavailable; QR scanning still works: {}".format(exc), flush=True)
    _map_qr_client = None


def report_qr_map_markers(ids):
    global _last_qr_map_id, _last_qr_map_at
    if not ids or _map_qr_client is None or not _map_qr_client.is_connected():
        return
    marker_id = int(ids[0])
    now = time.monotonic()
    if marker_id == _last_qr_map_id and now - _last_qr_map_at < 5.0:
        return
    info = _map_qr_client.publish(
        "smartfleet/robot/{}/aruco".format(MAP_ROBOT_CODE),
        json.dumps({"arucoId": marker_id}), qos=1, retain=False)
    if info.rc == mqtt.MQTT_ERR_SUCCESS:
        _last_qr_map_id = marker_id
        _last_qr_map_at = now
        print("[MAP] Sent ArUco {} to BE".format(marker_id), flush=True)


'''
        s = s.replace(anchor, helper + anchor)
        changed = True

    if "report_qr_map_markers(detected_ids)" not in s:
        anchor = "            detected_ids = current_ids\n"
        if s.count(anchor) != 1:
            raise RuntimeError("qr_scanning_code.py fresh IDs anchor missing")
        s = s.replace(anchor, anchor + "            report_qr_map_markers(detected_ids)\n")
        changed = True

    if "_map_qr_client.loop_stop()" not in s:
        anchor = "    cancel_circle_and_stop()\n"
        if s.count(anchor) != 1:
            raise RuntimeError("qr_scanning_code.py finally cleanup anchor missing")
        s = s.replace(anchor, anchor + '''    if _map_qr_client is not None:
        try:
            _map_qr_client.loop_stop()
            _map_qr_client.disconnect()
        except Exception:
            pass
''', 1)
        changed = True

    if changed:
        save(p, s, "Report position from existing QR ArUco Jupyter scanner")
    else:
        print("[SKIP] QR scanner already patched")


def main():
    for needed in (
        "SmartFleetBE/Controllers/WarehouseMapsController.cs",
        "SmartFleetBE/Services/MQTT/MqttArucoPositionWorker.cs",
        "SmartFleetBE/Database/aruco_map_schema.sql",
        "FrontEnd for SmartFleet/src/pages/WarehouseMapPage.tsx",
        "Robot code/aruco_live_map_jupyter.py",
    ):
        require_file(needed)
    patch_dbcontext()
    patch_program()
    patch_frontend_app()
    patch_sidebar()
    patch_robot_main()
    patch_qr_scanner()
    print("\nAll integration points patched. NEXT: run SQL, start BE/FE, and scan marker 1.")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print("\n[STOPPED] {}".format(exc), file=sys.stderr)
        sys.exit(1)
