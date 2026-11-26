# -*- coding: utf-8 -*-
"""SmartFleet Webots: camera ArUco navigation + complete MQTT transport mission.

Based on the user's WORKING Webots camera/IMU/turning controller (QR IDs 0..8).
This is the replacement for the previous *encoder-odometry* pilot controller.

A Backend route (WAYPOINT_ROUTE) is the ONLY source of mission waypoints.
The physical motion logic remains camera ArUco / IMU based; no simulated
coordinate arrival or timer-based fake completion. One Webots step thread.

Dependency: the SmartFleet QR full-mission Backend integration already installed.
Intended exclusively for an empty one-robot Webots pilot; NOT the real JetRacer.
"""

import cv2
import numpy as np
import math
import os
import sys
import json
import queue
import time
import uuid
import datetime as sf_datetime
import paho.mqtt.client as mqtt
from controller import Robot, Keyboard
from aruco_localization import PoseEstimator, HeadingFusion

# ============================================================
# TEST CONFIGURATION - verify before a mission
# ============================================================
BROKER_IP = os.environ.get("SMARTFLEET_MQTT_HOST", "localhost")
BROKER_PORT = int(os.environ.get("SMARTFLEET_MQTT_PORT", "1883"))
# SMARTFLEET_MULTI_ROBOT_ARGS_V1
# Each simulated robot is a separate process but shares this source file.
# Configure Robot.controllerArgs per robot in Webots, for example:
#   --robot-code=R01
#   --robot-code=R02
# The fallback below preserves the original one-robot R01 demo; it must NEVER
# be relied upon when multiple robots are present.
def _sf_controller_arg(key, default):
    prefix = "--{}=".format(key)
    matches = [value[len(prefix):] for value in sys.argv[1:]
               if value.startswith(prefix)]
    if len(matches) > 1:
        raise ValueError("Duplicated Webots controller argument " + prefix)
    return matches[0] if matches else default


ROBOT_CODE = _sf_controller_arg("robot-code", "R01").upper()
if not ROBOT_CODE.startswith("R") or not ROBOT_CODE[1:].isdigit():
    raise ValueError("Invalid Webots --robot-code: " + repr(ROBOT_CODE))
if not any(value.startswith("--robot-code=") for value in sys.argv[1:]):
    print("[CONFIG WARNING] controllerArgs has no --robot-code; using R01! "
          "Set a unique code on EVERY Webots Robot before adding more.")
# Start pose is measured from camera/IMU, never from controller arguments.
BROKER_IP = _sf_controller_arg("broker-host", BROKER_IP)
BROKER_PORT = int(_sf_controller_arg("broker-port", str(BROKER_PORT)))
localization_diagnostics_dir = _sf_controller_arg("localization-diagnostics-dir", os.environ.get("SMARTFLEET_DIAGNOSTICS_DIR", ""))
if any(v.startswith(("--start-heading=", "--start-qr=")) for v in sys.argv[1:]):
    print("[CONFIG] Deprecated start-heading/start-qr ignored; using ArUco + IMU")
# WEBOTS ONLY. Never enable fake sensor CLEAR on a real JetRacer.
INTERSECTION_PILOT = _sf_controller_arg("intersection-pilot", "false").lower() in ("true", "1", "yes")
SF_FAKE_CLEAR = _sf_controller_arg("sim-lidar", "false").lower() in ("true", "1", "yes")
SF_NAV_ACK_TIMEOUT = 15.0
SF_NAV_ACK_RETRY = 1.5
SF_LIDAR_INTERVAL = 0.8
SF_TEST_HOLD_SECONDS = max(0.0, min(30.0, float(_sf_controller_arg("traffic-test-hold", "0"))))
READY_HEARTBEAT_SECONDS = 10.0
SIMULATED_BATTERY_PERCENT = 85.0  # Webots demo telemetry, NOT a real battery measurement
SIMULATED_BATTERY_VOLTAGE = 11.6
MAX_SIM_SECONDS_PER_WAYPOINT = 150.0
READY_RETRY_SECONDS = 4.0
ACK_RETRY_SECONDS = 3.0
ACK_TIMEOUT_SECONDS = 22.0

COMMAND_TOPIC = "smartfleet/robot/{}/command".format(ROBOT_CODE)
STATUS_TOPIC = "smartfleet/robot/{}/status".format(ROBOT_CODE)
STATUS_ACK_TOPIC = "smartfleet/robot/{}/status-ack".format(ROBOT_CODE)
QR_PROGRESS_TOPIC = "smartfleet/robot/{}/qr-progress".format(ROBOT_CODE)
LOCALIZATION_MAP_TOPIC = "smartfleet/robot/{}/localization-map".format(ROBOT_CODE)
QR_PROGRESS_ACK_TOPIC = "smartfleet/robot/{}/qr-progress-ack".format(ROBOT_CODE)
TELEMETRY_TOPIC = "smartfleet/robot/{}/telemetry".format(ROBOT_CODE)
SF_SCAN_TOPIC = "smartfleet/robot/{}/scan".format(ROBOT_CODE)
SF_NAV_EVENT_TOPIC = "smartfleet/robot/{}/navigation".format(ROBOT_CODE)
SF_NAV_ACK_TOPIC = "smartfleet/robot/{}/navigation-ack".format(ROBOT_CODE)
SF_NAV_CONTROL_TOPIC = "smartfleet/robot/{}/nav-control".format(ROBOT_CODE)
SF_LIDAR_TOPIC = "smartfleet/robot/{}/lidar".format(ROBOT_CODE)

# ============================================================
# WEBOTS DEVICES - original working controller
# ============================================================
robot = Robot()
timestep = int(robot.getBasicTimeStep())

a_camera = robot.getDevice("camera")
camera = a_camera
camera.enable(timestep)
keyboard = Keyboard()
keyboard.enable(timestep)

left_motor = robot.getDevice("left wheel motor")
right_motor = robot.getDevice("right wheel motor")
left_motor.setPosition(float("inf"))
right_motor.setPosition(float("inf"))
left_motor.setVelocity(0.0)
right_motor.setVelocity(0.0)
left_encoder = robot.getDevice("left wheel sensor")
right_encoder = robot.getDevice("right wheel sensor")
left_encoder.enable(timestep)
right_encoder.enable(timestep)

imu = robot.getDevice("inertial unit")
imu.enable(timestep)
SF_REAL_SIM_LIDAR = os.environ.get("SMARTFLEET_REAL_SIM_LIDAR", "false").lower() == "true"
sim_lidar = robot.getDevice("LDS-01") if SF_REAL_SIM_LIDAR else None
if sim_lidar is not None:
    sim_lidar.enable(timestep)
sf_local_obstacle = "NO_SCAN_DATA" if SF_REAL_SIM_LIDAR else "CLEAR"

aruco_dict = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_250)
aruco_params = cv2.aruco.DetectorParameters()
aruco_params.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
aruco_params.cornerRefinementWinSize = 3
detector = cv2.aruco.ArucoDetector(aruco_dict, aruco_params)

# ============================================================
# No warehouse/QR adjacency table lives in the controller.
# Backend map waypoints include a travelDirection for each road link.
# The controller only performs generic camera/IMU steering and validates
# each Backend direction against the Backend-provided map grid positions.
# ============================================================
DIRECTION_YAW = {"N": 0.0, "W": 90.0, "S": 180.0, "E": -90.0}

# Original working motion tuning (all wheel speeds under 6.67 rad/s)
ROBOT_MAX_VEL = 6.0
MAX_SPEED = 3.5
TURN_SPEED = 3.0
OVERSHOOT_TIME = 0.45
STABLE_FRAMES = 3
SLOWDOWN_THRESHOLD = 25.0
MIN_TURN_SPEED = 0.8
ALIGN_SPEED = 2.0
KP_STRAIGHT = 0.05
KP_ALIGN_OFFSET = 2.5
KP_ALIGN_TILT = 0.03

# CAMERA CHECKPOINT APPROACH FIX (Webots pilot tuning)
# Apparent marker size = median corner-to-corner side length / image width.
# These are IMAGE fractions, not meters. Tune from [APPROACH] logs.
# With your current camera, a marker near the robot was about 0.17 wide.
# ============================================================
CRUISE_SCALE = float(os.environ.get("SMARTFLEET_CRUISE_SCALE", _sf_controller_arg("cruise-scale", "1.0")))
if not math.isfinite(CRUISE_SCALE) or not .5 <= CRUISE_SCALE <= 1.5:
    raise ValueError("Simulation cruise scale must be between 0.5 and 1.5")
SEEK_SPEED = 2.2 * CRUISE_SCALE  # cruise only; near-QR/turn/overshoot tuning stays unchanged
APPROACH_SPEED = 1.35            # marker visible and approaching
FINAL_APPROACH_SPEED = 0.70     # very close to marker
RECOVERY_CRAWL_SPEED = 0.65     # briefly reacquire a far marker
APPROACH_VISIBLE_FRACTION = 0.055
MARKER_NEAR_FRACTION = 0.090
ARRIVAL_MARKER_FRACTION = 0.130
ARRIVAL_MAX_OFFSET = 0.65       # normalised [-1,1] image-center offset
LOST_NEAR_FRAMES = 24           # stop immediately if a near marker disappears
LOST_FAR_FRAMES = 36            # crawl briefly, then stop if still lost
STEERING_FRACTION_LIMIT = 0.55 # no full-speed reverse wheel during approach

STATE_SEEKING = "SEEKING"
STATE_ALIGNING = "ALIGNING"
STATE_OVERSHOOT = "OVERSHOOT"
STATE_TURNING = "TURNING"
STATE_IDLE = "IDLE"

# ============================================================
# MOTION STATE - from the original working controller
# ============================================================
current_state = STATE_SEEKING
state_start_time = 0.0
stable_counter = 0
last_detected_id = -1
missing_align_frames = 0

# Per-segment camera approach state. Reset for every Backend waypoint.
approach_seen = False
approach_near = False
approach_lost_frames = 0
approach_max_size = 0.0
approach_last_band = None

current_marker = None
current_heading = "S"
base_yaw_offset = 0.0
is_first_checkin = True

target_yaw_deg = 0.0
turn_direction = "LEFT"

path_queue = []  # IDs remaining on the Backend route (never computed locally)
current_target = None
is_running = True

# ============================================================
# MQTT / MISSION STATE
# ============================================================
incoming = queue.Queue()
connected = False
mission = None
pending = None
ready_event_id = None
ready_acknowledged = False
ready_last_sent = 0.0
last_telemetry_at = -100.0
mission_camera_lost_since = None
localization_map = None
localization_request_id = None
localization_request_at = -100.0
localization_last_frame_at = -100.0
localization_last_warning_at = -100.0
localization_reconnect = False
localization_valid = False
localization_reason = "Waiting for camera pose"
heading_fusion = HeadingFusion()
pose_estimator = None
try:
    calibration_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "aruco_camera_calibration.json")
    with open(calibration_path, encoding="utf-8") as handle:
        pose_estimator = PoseEstimator(json.load(handle), camera.getWidth(), camera.getHeight(), camera.getFov())
except (OSError, ValueError, KeyError, TypeError) as error:
    print("[LOCALIZATION CONFIG]", error)
last_bootstrap_warning_at = 0.0
sf_nav_epoch = None
sf_nav_sequence = -1
sf_nav_directive = None
sf_nav_notice = None
sf_nav_request = None
sf_active_intersection = None
sf_last_lidar_at = 0.0
sf_last_hold_log = 0.0
sf_reconnected_with_mission = False


def set_motor_speeds(v_left, v_right):
    """Working wheel control, capped at ROBOT_MAX_VEL."""
    if INTERSECTION_PILOT and mission is not None and mission.get("phase") == "MOVING":
        factor = sf_wheel_scale()
        v_left *= factor
        v_right *= factor
    v_l = max(-ROBOT_MAX_VEL, min(ROBOT_MAX_VEL, v_left))
    v_r = max(-ROBOT_MAX_VEL, min(ROBOT_MAX_VEL, v_right))
    left_motor.setVelocity(v_l)
    right_motor.setVelocity(v_r)


def stop_robot():
    set_motor_speeds(0.0, 0.0)


def normalize_angle(angle):
    while angle > 180.0:
        angle -= 360.0
    while angle < -180.0:
        angle += 360.0
    return angle


def get_camera_frame():
    try:
        img = camera.getImage()
        if img is None:
            return None
        w, h = camera.getWidth(), camera.getHeight()
        arr = np.frombuffer(img, dtype=np.uint8).reshape((h, w, 4))
        return cv2.cvtColor(arr, cv2.COLOR_BGRA2BGR)
    except Exception:
        return None


def get_yaw_deg():
    return imu.getRollPitchYaw()[2] * 180.0 / math.pi


def get_marker_offset_tilt(corners, img_width):
    pts = corners[0][0]
    cx = np.mean(pts[:, 0])
    offset_x = (cx - img_width / 2) / (img_width / 2)
    dx = pts[1][0] - pts[0][0]
    dy = pts[1][1] - pts[0][1]
    tilt = math.degrees(math.atan2(dy, dx))
    if tilt > 90:
        tilt -= 180
    elif tilt < -90:
        tilt += 180
    return offset_x, tilt


def marker_size_fraction(corners, frame_width):
    """Approximate proximity using observed marker size (not metric distance).

    The average of the four projected side lengths handles mild perspective
    distortion better than horizontal width alone. This approach controller uses
    image fractions as proximity cues; pose estimation runs separately.
    """
    points = np.asarray(corners[0][0], dtype=float)
    if points.shape != (4, 2) or frame_width <= 0:
        return 0.0
    sides = [float(np.linalg.norm(points[(i + 1) % 4] - points[i]))
             for i in range(4)]
    return float(np.median(sides)) / float(frame_width)


def visual_approach(data, target_id):
    """Single camera-arrival check for BOTH SEEKING and ALIGNING states.

    A detected QR is not 'reached' until it is close and seen stably while
    reasonably centered. If a nearby QR vanishes, stop rather than driving
    through it at SEEKING speed.
    """
    global current_state, stable_counter, last_detected_id
    global approach_seen, approach_near, approach_lost_frames
    global approach_max_size, approach_last_band

    observation = visible_marker(target_id)
    if observation is not None:
        data.pop('qr_reacquire_at', None)
        corners, width = observation
        offset_x, tilt = get_marker_offset_tilt(corners, width)
        size = marker_size_fraction(corners, width)
        approach_seen = True
        approach_lost_frames = 0
        approach_max_size = max(approach_max_size, size)
        last_detected_id = target_id
        current_state = STATE_ALIGNING

        if size >= MARKER_NEAR_FRACTION:
            approach_near = True

        if size >= ARRIVAL_MARKER_FRACTION and abs(offset_x) <= ARRIVAL_MAX_OFFSET:
            # Stop first, THEN confirm over repeated camera frames. We should
            # never drive forward while collecting arrival confirmations.
            stop_robot()
            stable_counter += 1
            if approach_last_band != 'ARRIVAL':
                print('[APPROACH] QR{} arrival zone size={:.3f} offset={:+.2f}'
                      .format(target_id, size, offset_x))
                approach_last_band = 'ARRIVAL'
            if stable_counter >= STABLE_FRAMES:
                reached_marker(data, target_id)
            return

        stable_counter = 0
        if approach_near or size >= MARKER_NEAR_FRACTION:
            # Do not accelerate because of one small/jittery marker frame.
            band = 'NEAR'
            speed = FINAL_APPROACH_SPEED
        elif size >= APPROACH_VISIBLE_FRACTION:
            band = 'SLOW'
            speed = APPROACH_SPEED
        else:
            band = 'FAR'
            speed = SEEK_SPEED

        if band != approach_last_band:
            print('[APPROACH] QR{} {} size={:.3f} offset={:+.2f} speed={:.2f}'
                  .format(target_id, band, size, offset_x, speed))
            approach_last_band = band

        # If the marker is already huge and far off-center, moving blindly
        # can drive right past it; halt rather than falsely confirm arrival.
        if size >= ARRIVAL_MARKER_FRACTION * 1.40 and abs(offset_x) > ARRIVAL_MAX_OFFSET:
            fail_mission('QR{} very close but outside centered arrival zone; '
                         'stop and re-align camera/robot'.format(target_id))
            return

        correction = KP_ALIGN_OFFSET * offset_x + KP_ALIGN_TILT * tilt
        max_correction = STEERING_FRACTION_LIMIT * speed
        correction = max(-max_correction, min(max_correction, correction))
        set_motor_speeds(speed - correction, speed + correction)
        return

    # Camera cannot see the expected marker in this frame.
    stable_counter = 0
    last_detected_id = -1
    approach_lost_frames += 1

    if approach_near or approach_seen:
        # The last view indicated the robot was close. Do not accelerate
        # into a blind, full-speed search across the actual QR location.
        stop_robot()
        if approach_lost_frames == 1:
            print('[APPROACH] Lost NEAR QR{}; holding still for reacquisition'
                  .format(target_id))
        lost_at = data.setdefault('qr_reacquire_at', time.monotonic())
        if time.monotonic() - lost_at >= 8.0:
            fail_mission('Camera lost nearby QR{} before confirming arrival; '
                         'robot stopped safely'.format(target_id))
        return

    # The marker has not yet been observed on this straight corridor.
    # Continue cautiously in the Backend-provided cardinal direction.
    drive_straight_with_imu(SEEK_SPEED)



def decide_turn(direction_to_go):
    """Use measured continuous heading; a 45/180 degree cold start needs no manual hint."""
    global current_heading
    if direction_to_go not in DIRECTION_YAW or not localization_valid:
        raise ValueError("No valid camera/IMU orientation for this route")
    difference = normalize_angle(DIRECTION_YAW[direction_to_go] + base_yaw_offset - get_yaw_deg())
    current_heading = direction_to_go
    if abs(difference) <= 1.5:
        return 0.0, None
    return abs(difference), "LEFT" if difference > 0 else "RIGHT"



def get_absolute_target_yaw():
    return normalize_angle(DIRECTION_YAW[current_heading] + base_yaw_offset)


def drive_straight_with_imu(base_speed):
    current_yaw = get_yaw_deg()
    target_yaw = get_absolute_target_yaw()
    error = normalize_angle(target_yaw - current_yaw)
    correction = KP_STRAIGHT * error
    set_motor_speeds(base_speed - correction, base_speed + correction)


def visible_marker(expected_id):
    """Find specifically the expected marker, even if multiple markers are seen."""
    frame = get_camera_frame()
    if frame is None:
        return None
    corners, ids, _ = detector.detectMarkers(frame)
    if ids is None:
        return None
    for i, value in enumerate(ids.flatten().tolist()):
        if int(value) == int(expected_id):
            return [corners[i]], camera.getWidth()
    return None


def valid_int(value):
    return type(value) is int and value >= 0


def expected_grid_direction(from_wp, to_wp):
    """Directional geometry comes exclusively from the Backend grid coordinates."""
    for waypoint in (from_wp, to_wp):
        if not valid_int(waypoint.get("row")) or not valid_int(waypoint.get("col")):
            raise ValueError("Backend waypoint needs nonnegative row/col")
    dr = to_wp["row"] - from_wp["row"]
    dc = to_wp["col"] - from_wp["col"]
    if dr == 0 and dc > 0:
        return "E"
    if dr == 0 and dc < 0:
        return "W"
    if dc == 0 and dr > 0:
        return "S"
    if dc == 0 and dr < 0:
        return "N"
    raise ValueError("Linked QR nodes must be distinct and grid-axis aligned")


def visible_any_marker():
    """Detect the largest camera-visible DICT_4X4_50 marker (no fixed QR ID)."""
    frame = get_camera_frame()
    if frame is None:
        return None
    corners, ids, _ = detector.detectMarkers(frame)
    if ids is None or len(ids) == 0:
        return None
    choices = []
    for i, value in enumerate(ids.flatten().tolist()):
        marker_id = int(value)
        if 0 <= marker_id <= 249:
            area = abs(cv2.contourArea(np.asarray(corners[i][0], dtype=np.float32)))
            choices.append((area, marker_id, [corners[i]]))
    if not choices:
        return None
    _, marker_id, selected = max(choices, key=lambda item: item[0])
    return marker_id, selected, camera.getWidth()








def store_heading_calibration(marker_id=None):
    """Correct slow IMU drift from a camera-observed marker, never from the planned heading."""
    global base_yaw_offset
    if not localization_valid or localization_map is None or pose_estimator is None:
        raise ValueError("Camera/IMU orientation is not initialized")
    marker_id = current_marker if marker_id is None else marker_id
    seen = visible_marker(marker_id)
    marker = next((m for m in localization_map["markers"] if m["arucoId"] == marker_id), None)
    if seen is None or marker is None:
        raise ValueError("Checkpoint marker is not camera-visible or commissioned")
    corners, width = seen
    pose = pose_estimator.estimate(corners[0], marker)
    delta = normalize_angle(pose["headingDeg"] - heading_fusion.heading(get_yaw_deg()))
    if abs(delta) > 10.0:
        raise ValueError("Camera/IMU orientation disagreement at checkpoint")
    heading_fusion.offset = normalize_angle(heading_fusion.offset - 0.2 * delta)
    base_yaw_offset = heading_fusion.offset



def validate_command(data):
    if not localization_valid or localization_map is None or data.get("mapRevision") != localization_map["mapRevision"]:
        raise ValueError("Fresh camera/IMU localization on this map is required")
    if abs(normalize_angle(heading_fusion.heading(get_yaw_deg()) - heading_fusion.last_vision_yaw)) > 5.0:
        raise ValueError("Robot was rotated after its last camera localization")
    if data.get("command") != "EXECUTE_TASK" or data.get("pattern") != "WAYPOINT_ROUTE":
        raise ValueError("Expected EXECUTE_TASK / WAYPOINT_ROUTE; CIRCLE is disabled")
    for key in ("taskId", "assignmentId", "missionId"):
        if not valid_int(data.get(key)) or data[key] < 1:
            raise ValueError("Missing or invalid " + key)
    if not valid_int(data.get("startArUcoId")):
        raise ValueError("Missing startArUcoId")
    if not valid_int(data.get("pickupArUcoId")) or not valid_int(data.get("deliveryArUcoId")):
        raise ValueError("Missing pickup or delivery QR")
    if not valid_int(data.get("pickupWaypointIndex")):
        raise ValueError("Missing pickupWaypointIndex")
    waypoints = data.get("waypoints")
    if not isinstance(waypoints, list) or not 2 <= len(waypoints) <= 100:
        raise ValueError("Require 2..100 waypoints")
    if not valid_int(data.get("mapRevision")) or data["mapRevision"] < 1:
        raise ValueError("Missing Backend map revision")
    ids = []
    for i, waypoint in enumerate(waypoints):
        if not isinstance(waypoint, dict) or not valid_int(waypoint.get("arucoId")):
            raise ValueError("Bad waypoint at index {}".format(i))
        ids.append(waypoint["arucoId"])
        if not valid_int(waypoint.get("row")) or not valid_int(waypoint.get("col")):
            raise ValueError("Waypoint missing Backend grid coordinates")
    if INTERSECTION_PILOT:
        sf_validate_intersection_waypoints(waypoints)
    if ids[0] != data["startArUcoId"] or ids[-1] != data["deliveryArUcoId"]:
        raise ValueError("Route endpoints mismatch")
    pi = data["pickupWaypointIndex"]
    if pi >= len(ids) or ids[pi] != data["pickupArUcoId"]:
        raise ValueError("pickupWaypointIndex mismatch")
    for index in range(1, len(waypoints)):
        from_wp, to_wp = waypoints[index - 1], waypoints[index]
        direction = expected_grid_direction(from_wp, to_wp)
        if to_wp.get("travelDirection") != direction:
            raise ValueError("Backend QR{} -> QR{} direction mismatch: expected {}, received {}".format(
                from_wp["arucoId"], to_wp["arucoId"],
                direction, to_wp.get("travelDirection")))
    if current_marker is None or data["startArUcoId"] != current_marker:
        raise ValueError("Camera is at QR{}, Backend route starts at QR{}".format(
            current_marker, data["startArUcoId"]))
    # Optional cross-checks: the BE's actual route legs must agree.
    if isinstance(data.get("routeToPickup"), list) and data["routeToPickup"] != ids[:pi+1]:
        raise ValueError("routeToPickup mismatch")
    if isinstance(data.get("routeToDelivery"), list) and data["routeToDelivery"] != ids[pi:]:
        raise ValueError("routeToDelivery mismatch")
    return data


# ============================================================
# MQTT CALLBACKS: only put messages into a queue; Webots motor calls
# and robot.step() happen on the Webots simulation thread.
# ============================================================
def on_connect(cli, userdata, flags, reason_code, properties=None):
    global connected, ready_acknowledged, ready_event_id, ready_last_sent
    global localization_reconnect, localization_request_at
    localization_reconnect = True
    localization_request_at = -100.0
    connected = reason_code == 0
    if connected:
        cli.subscribe([(COMMAND_TOPIC, 1), (STATUS_ACK_TOPIC, 1),
                       (QR_PROGRESS_ACK_TOPIC, 1), (SF_NAV_CONTROL_TOPIC, 1),
                       (SF_NAV_ACK_TOPIC, 1), (LOCALIZATION_MAP_TOPIC, 1)])
        # A reconnect is not proof that the Backend retained an in-memory READY.
        ready_acknowledged = False
        ready_event_id = None
        ready_last_sent = 0.0
        print("[MQTT] Connected for", ROBOT_CODE)
    else:
        print("[MQTT] Connection failed:", reason_code)


def on_disconnect(cli, userdata, disconnect_flags, reason_code, properties=None):
    global sf_reconnected_with_mission
    if INTERSECTION_PILOT and mission is not None:
        sf_reconnected_with_mission = True
    global connected
    connected = False
    print("[MQTT] Disconnected: motors will HOLD")


def on_message(cli, userdata, msg):
    if msg.retain:
        return
    try:
        incoming.put((msg.topic, json.loads(msg.payload.decode("utf-8"))))
    except (ValueError, UnicodeError) as ex:
        print("[MQTT] Invalid message:", ex)


client = mqtt.Client(
    callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
    client_id="smartfleet-webots-camera-{}-{}".format(ROBOT_CODE.lower(), uuid.uuid4().hex[:8]),
)
client.on_connect = on_connect
client.on_disconnect = on_disconnect
client.on_message = on_message
client.reconnect_delay_set(min_delay=1, max_delay=5)


def publish(topic, data, qos=1):
    if not connected:
        return False
    sent = client.publish(topic, json.dumps(data), qos=qos, retain=False)
    return sent.rc == mqtt.MQTT_ERR_SUCCESS


def send_ready():
    global ready_event_id, ready_last_sent
    if not connected or not localization_valid or is_first_checkin or current_marker is None or mission is not None:
        return
    if ready_event_id is None:
        ready_event_id = str(uuid.uuid4())
    if publish(QR_PROGRESS_TOPIC, {
        "event": "READY", "eventId": ready_event_id,
        "missionId": 0, "arucoId": current_marker, "waypointIndex": 0,
        "mapRevision": localization_map["mapRevision"],
        "headingDeg": heading_fusion.heading(get_yaw_deg()), "localizationSource": "ARUCO_IMU",
        "reprojectionErrorPx": heading_fusion.last_error, "stableFrames": len(heading_fusion.samples),
    }):
        ready_last_sent = time.monotonic()
        print("[READY] Camera confirmed QR{}; sent to Backend".format(current_marker))


def tick_ready():
    """Refresh READY in idle, including after a Backend restart or a new delivery."""
    global ready_event_id, ready_acknowledged
    if is_first_checkin or mission is not None or not connected:
        return
    age = time.monotonic() - ready_last_sent
    threshold = READY_HEARTBEAT_SECONDS if ready_acknowledged else READY_RETRY_SECONDS
    if age >= threshold:
        if ready_acknowledged:
            # Do not revoke an already verified QR while waiting for an idle
            # heartbeat ACK: the next task may be dispatched immediately.
            ready_event_id = None
        send_ready()


def send_reliable(kind, topic, data, ack_topic, index=None):
    global pending
    if not connected:
        raise RuntimeError("MQTT offline")
    if pending is not None:
        raise RuntimeError("Already awaiting an application ACK")
    body = dict(data)
    body["eventId"] = str(uuid.uuid4())
    pending = {
        "kind": kind, "topic": topic, "ack_topic": ack_topic,
        "body": body, "eventId": body["eventId"], "index": index,
        "deadline": time.monotonic() + ACK_TIMEOUT_SECONDS,
        "retry_at": time.monotonic() + ACK_RETRY_SECONDS,
        "accepted": None, "reason": "",
    }
    if not publish(topic, body):
        pending = None
        raise RuntimeError("Cannot publish " + kind)


def send_status(event, data):
    print("[STATUS]", event, "Mission", data["missionId"])
    send_reliable(event, STATUS_TOPIC, {
        "event": event, "taskId": data["taskId"],
        "assignmentId": data["assignmentId"], "missionId": data["missionId"],
    }, STATUS_ACK_TOPIC)


def send_progress(data, index):
    waypoint_id = data["waypoints"][index]["arucoId"]
    print("[QR] CAMERA reached QR{} (waypoint {})".format(waypoint_id, index))
    send_reliable("WAYPOINT_REACHED", QR_PROGRESS_TOPIC, {
        "event": "WAYPOINT_REACHED", "missionId": data["missionId"],
        "arucoId": waypoint_id, "waypointIndex": index,
        "mapRevision": data["mapRevision"], "robotStopped": True,
        "headingDeg": heading_fusion.heading(get_yaw_deg()),
    }, QR_PROGRESS_ACK_TOPIC, index=index)


def poll_verification(data):
    index = data["next_index"] - 1
    # A live camera observation is required while confirming physical transfer.
    if visible_marker(current_marker) is None:
        stop_robot()
        return
    send_reliable("VERIFICATION_STATUS", QR_PROGRESS_TOPIC, {
        "event": "VERIFICATION_STATUS", "missionId": data["missionId"],
        "arucoId": current_marker, "waypointIndex": index,
        "mapRevision": data["mapRevision"], "robotStopped": True,
        "headingDeg": heading_fusion.heading(get_yaw_deg()),
    }, QR_PROGRESS_ACK_TOPIC, index=index)
    data["verification_poll_at"] = time.monotonic() + 2.0


def publish_failure(data, reason):
    if data.get("chargingJobId"):
        charging_fault(data, reason)
        return
    if connected:
        publish(STATUS_TOPIC, {
            "eventId": str(uuid.uuid4()), "event": "MISSION_FAILED",
            "taskId": data["taskId"], "assignmentId": data["assignmentId"],
            "missionId": data["missionId"], "reason": str(reason)[:450],
        })


def fail_mission(reason, notify_backend=True):
    global sf_nav_request, sf_reconnected_with_mission
    sf_nav_request = None
    sf_reconnected_with_mission = False
    global mission, pending, current_state, is_first_checkin
    global ready_acknowledged, ready_event_id, ready_last_sent
    stop_robot()
    print("[MISSION HALTED]", reason)
    if mission is not None and notify_backend:
        publish_failure(mission, reason)
    mission, pending = None, None
    current_state = STATE_IDLE
    # Last confirmed QR may be behind the robot. Require a fresh camera pose
    # before reporting READY for another route after an unsafe interruption.
    is_first_checkin = True
    ready_acknowledged = False
    ready_event_id = None
    ready_last_sent = 0.0


def start_mission(data):
    global mission, current_state, pending, path_queue, current_target
    global charging_dock
    if charging_saved.get("state") in ("ACTIVE", "PAUSED", "FAILED"):
        raise ValueError("Charging recovery is held; Maintenance must safely resume it")
    if mission is not None:
        if data.get("missionId") == mission.get("missionId"):
            print("[MISSION] Duplicate command ignored")
        else:
            print("[MISSION] Busy; another mission command ignored")
        return
    if is_first_checkin or not ready_acknowledged:
        raise ValueError("Robot has not yet camera-detected START QR / READY was not ACKed")
    validate_command(data)
    if INTERSECTION_PILOT and sf_active_intersection is not None:
        raise ValueError("Previous intersection reservation still active; investigate/clear physically")
    mission = dict(data)
    if charging_dock is not None:
        save_charging(charging_dock, "COMPLETED")
        charging_dock = None
    mission["phase"] = "STARTING"
    mission["next_index"] = 0
    mission["segment_started_at"] = robot.getTime()
    current_state = STATE_IDLE
    current_target = data["deliveryArUcoId"]
    path_queue = [w["arucoId"] for w in data["waypoints"][1:]]
    stop_robot()
    print("\n====== SMARTFLEET WEBOTS FULL TRANSPORT MISSION ======")
    print("Task {} | Assignment {} | Mission {}".format(
        data["taskId"], data["assignmentId"], data["missionId"]))
    print("Pickup QR{} | Drop-off QR{}".format(data["pickupArUcoId"], data["deliveryArUcoId"]))
    print("Backend route:", " -> ".join("QR{}".format(w["arucoId"]) for w in data["waypoints"]))
    send_status("MISSION_STARTED", mission)


# Safety controls are latched across controller restarts. Only a confirmed HOME
# return clears this latch; transport EXECUTE_TASK never clears it.
SAFETY_DIRECTORY = os.environ.get("SMARTFLEET_STATE_DIR", os.path.dirname(os.path.abspath(__file__)))
os.makedirs(SAFETY_DIRECTORY, exist_ok=True)
SAFETY_FILE = os.path.join(SAFETY_DIRECTORY, ".smartfleet-safety-" + ROBOT_CODE + ".json")
safety_hold = os.path.exists(SAFETY_FILE)
safety_commands = {}
safety_generation = {}
if safety_hold:
    try:
        with open(SAFETY_FILE, "r", encoding="utf-8") as handle:
            saved_safety = json.load(handle)
        safety_generation[saved_safety["taskId"]] = saved_safety.get("generation", 0)
    except (OSError, ValueError, KeyError):
        pass  # corrupt state still holds motors

def latch_safety(data):
    global safety_hold
    safety_hold = True
    with open(SAFETY_FILE, "w", encoding="utf-8") as handle:
        json.dump({"commandId": data.get("commandId"), "taskId": data.get("taskId"),
                   "generation": data.get("generation", 0)}, handle)


def safety_ack(data, event="CONTROL_STOPPED", marker=None):
    publish(STATUS_TOPIC, {"eventId": str(uuid.uuid4()), "event": event,
        "taskId": data["taskId"], "assignmentId": data["assignmentId"],
        "missionId": data["missionId"], "commandId": data["commandId"],
        "currentArUcoId": marker})


def handle_safety_command(data):
    global mission, pending, current_state, safety_hold
    global sf_nav_request, sf_nav_directive
    command_id = data.get("commandId")
    if not isinstance(command_id, str) or not command_id:
        return
    generation = data.get("generation")
    if not valid_int(generation) or generation < 1:
        return
    task_id = data.get("taskId")
    if generation < safety_generation.get(task_id, 0):
        return  # delayed RETURN_HOME must not override a newer Pause
    # Correlate control with this mission; stale controls must never redirect a new mission.
    if mission is not None and data.get("missionId") != mission.get("missionId"):
        return
    if command_id in safety_commands:
        event, marker = safety_commands[command_id]
        safety_ack(data, event, marker)
        return
    kind = data.get("command")
    if kind in ("PAUSE_TASK", "CANCEL_STOP", "FORCE_CANCEL"):
        stop_robot()
        safety_generation[task_id] = generation
        latch_safety(data)
        pending = None
        # Last scanned QR is NOT a fresh position while between waypoints.
        marker = current_marker if current_marker is not None and visible_marker(current_marker) is not None else None
        safety_commands[command_id] = ("CONTROL_STOPPED", marker)
        safety_ack(data, marker=marker)
        return
    if kind == "RETURN_HOME":
        if mission is not None and mission.get("home_command_id") == command_id:
            return  # retry must not reset a moving home route
        stop_robot()
        route = data.get("route")
        if not safety_hold or not isinstance(route, dict):
            return
        waypoints = route.get("waypoints")
        if not isinstance(waypoints, list) or not waypoints:
            return
        if route.get("startArUcoId") != current_marker or visible_marker(current_marker) is None:
            return  # no fresh camera pose => remain held for physical recovery
        if waypoints[0].get("arucoId") != current_marker or waypoints[-1].get("arucoId") != route.get("homeArUcoId"):
            return
        for i in range(1, len(waypoints)):
            if waypoints[i].get("travelDirection") != expected_grid_direction(waypoints[i-1], waypoints[i]):
                return
        if INTERSECTION_PILOT:
            try:
                if not localization_valid or localization_map is None or route.get("mapRevision") != localization_map["mapRevision"]:
                    raise ValueError("fresh calibrated map/heading is required for return home")
                sf_validate_intersection_waypoints(waypoints, allow_stationary=True)
                if sf_active_intersection is not None:
                    raise ValueError("active intersection reservation requires recovery")
            except ValueError as ex:
                print("[SAFETY HOLD] RETURN_HOME not safe under traffic protocol:", ex)
                return
        mission = dict(data)
        safety_generation[task_id] = generation
        latch_safety(data)
        mission.update({"waypoints": waypoints, "deliveryArUcoId": route["homeArUcoId"],
                        "home_command_id": command_id, "phase": "MOVING", "next_index": 1})
        pending = None
        sf_nav_request = None
        sf_nav_directive = None  # A fresh grant for the HOME route is mandatory.
        safety_hold = False  # persistent disk latch stays until physical HOME
        if len(waypoints) == 1:
            finish_home(mission)
        else:
            begin_segment(mission)


def finish_home(data):
    global mission, pending, safety_hold, current_state
    stop_robot()
    safety_hold = True  # release only when BE ACKs physical home arrival
    send_reliable("HOME_REACHED", STATUS_TOPIC, {
        "event": "HOME_REACHED", "taskId": data["taskId"],
        "assignmentId": data["assignmentId"], "missionId": data["missionId"],
        "commandId": data["home_command_id"], "currentArUcoId": current_marker,
    }, STATUS_ACK_TOPIC)
    data["phase"] = "HOME_ACK"
    current_state = STATE_IDLE


from charging_protocol import ChargingJournal, validate_route as validate_charging_route
from motion_safety import front_lidar_report
charging_journal = ChargingJournal(os.path.join(SAFETY_DIRECTORY, '.smartfleet-charging-' + ROBOT_CODE + '.json'))
charging_saved = charging_journal.saved
charging_dock = None
charging_fault_report = None
charging_last_report = 0.0


def save_charging(data, state):
    global charging_saved
    charging_journal.save(data, state)
    charging_saved = charging_journal.saved


def charging_body(data, index, event='CHARGE_WAYPOINT'):
    return {'event': event, 'missionId': 0, 'chargingJobId': data['chargingJobId'],
            'generation': data['generation'], 'arucoId': data['waypoints'][index]['arucoId'],
            'waypointIndex': index, 'mapRevision': data['mapRevision'],
            'robotStopped': True, 'headingDeg': heading_fusion.heading(get_yaw_deg())}


def send_charging_progress(data, index):
    send_reliable('CHARGE_WAYPOINT', QR_PROGRESS_TOPIC, charging_body(data,index),QR_PROGRESS_ACK_TOPIC,index=index)
    data['phase'] = 'WAIT_CHARGE_ACK'


def finish_charging_arrival(data):
    global mission, pending, charging_dock, current_state, ready_acknowledged, ready_event_id, ready_last_sent
    stop_robot()
    save_charging(data,'DOCKED')
    charging_dock = dict(data)
    mission, pending = None, None
    current_state = STATE_IDLE
    ready_acknowledged = False
    ready_event_id = None
    ready_last_sent = 0.0
    print('[CHARGING] Camera-confirmed berth QR{}; stopped. Backend checks safety and >=20% battery.'.format(current_marker))


def charging_fault(data, reason):
    global charging_fault_report, charging_dock
    stop_robot()
    save_charging(data,'FAILED')
    charging_dock = None
    charging_fault_report = {'event':'CHARGE_FAILED','chargingJobId':data['chargingJobId'],
        'generation':data['generation'],'reason':str(reason)[:450], 'eventId':str(uuid.uuid4())}
    publish(QR_PROGRESS_TOPIC,charging_fault_report)


def handle_charging_command(data):
    global mission, pending, charging_dock, charging_fault_report, current_state, sf_nav_request, sf_nav_directive
    gen = data.get('generation')
    same = data.get('chargingJobId') == charging_saved.get('chargingJobId') and gen == charging_saved.get('generation')
    if data.get('command') == 'CHARGE_STOP':
        if not same and (not charging_journal.accepts_new(data) or mission is not None): return
        if same and charging_saved.get('state') == 'COMPLETED': return
        stop_robot()
        save_charging(data,'PAUSED')
        mission, pending, charging_dock = None,None,None
        charging_fault_report = None
        sf_nav_request, sf_nav_directive = None,None
        current_state = STATE_IDLE
        publish(QR_PROGRESS_TOPIC,{'event':'CHARGE_STOPPED','chargingJobId':data['chargingJobId'],
            'generation':gen,'robotStopped':True,'eventId':str(uuid.uuid4())})
        return
    if same:
        if mission is not None or charging_saved.get('state') in ('DOCKED','PAUSED','FAILED','COMPLETED'): return
        # A simulator/controller restart is not proof that a reserved road is clear.
        charging_fault(data,'Controller restarted during charging movement; Maintenance recovery required')
        return
    if not charging_journal.accepts_new(data) or mission is not None or safety_hold: return
    if not localization_valid or localization_map is None or visible_marker(current_marker) is None or not ready_acknowledged:
        return
    points = validate_charging_route(data,current_marker,localization_map['mapRevision'])
    sf_validate_intersection_waypoints(points,allow_stationary=True)
    if sf_active_intersection is not None: raise ValueError('Intersection reservation requires physical recovery')
    mission = dict(data)
    mission.update({'missionId':0,'mapRevision':data['route']['mapRevision'],'waypoints':points,
                    'deliveryArUcoId':data['route']['homeArUcoId'],'next_index':0})
    save_charging(data,'ACTIVE')
    charging_dock,charging_fault_report = None,None
    sf_nav_request,sf_nav_directive = None,None
    stop_robot()
    send_charging_progress(mission,0)


def tick_charging():
    global charging_last_report, SIMULATED_BATTERY_PERCENT
    if not connected or time.monotonic()-charging_last_report < 2: return
    charging_last_report=time.monotonic()
    if charging_fault_report is not None:
        publish(QR_PROGRESS_TOPIC,charging_fault_report)
    if charging_dock is None or safety_hold or mission is not None or not localization_valid or not ready_acknowledged or current_marker is None: return
    if current_marker != charging_dock['waypoints'][-1]['arucoId']:
        charging_fault(charging_dock,'Robot is localized outside its reserved charging bay; physical inspection required')
        return
    if visible_marker(current_marker) is None: return
    stop_robot()
    # Explicit Webots-only model: physical charging needs a real dock/BMS handshake.
    SIMULATED_BATTERY_PERCENT=round(min(100.,SIMULATED_BATTERY_PERCENT+.1),1)
    body=charging_body(charging_dock,len(charging_dock['waypoints'])-1,'CHARGE_DOCK_STATUS')
    body['eventId']=str(uuid.uuid4())
    publish(QR_PROGRESS_TOPIC,body)


def handle_incoming():
    global ready_acknowledged, ready_event_id, ready_last_sent, pending
    while True:
        try:
            topic, data = incoming.get_nowait()
        except queue.Empty:
            break
        if not isinstance(data, dict):
            continue
        if topic == LOCALIZATION_MAP_TOPIC:
            accept_localization_map(data)
        elif topic == COMMAND_TOPIC:
            if data.get("command") in ("CHARGE_ROUTE", "CHARGE_STOP"):
                try:
                    handle_charging_command(data)
                except (ValueError, RuntimeError, KeyError) as ex:
                    print("[CHARGING REJECTED]", ex)
                continue
            if data.get("command") in ("PAUSE_TASK", "CANCEL_STOP", "FORCE_CANCEL", "RETURN_HOME"):
                try:
                    handle_safety_command(data)
                except Exception as ex:
                    stop_robot()
                    latch_safety(data)
                    pending = None
                    print("[SAFETY HOLD]", ex)
                continue
            if safety_hold:
                continue
            if data.get("command") != "EXECUTE_TASK":
                continue
            try:
                start_mission(data)
            except (ValueError, RuntimeError) as ex:
                print("[COMMAND REJECTED]", ex)
                if mission is None and all(valid_int(data.get(k)) for k in
                                           ("taskId", "assignmentId", "missionId")):
                    publish_failure(data, "Rejected route: " + str(ex))
        elif topic == SF_NAV_CONTROL_TOPIC:
            sf_accept_nav_directive(data)
        elif topic == SF_NAV_ACK_TOPIC:
            sf_accept_nav_ack(data)
        elif topic == QR_PROGRESS_ACK_TOPIC and ready_event_id is not None and \
                data.get("eventId") == ready_event_id:
            if data.get("accepted") is True:
                ready_acknowledged = True
                ready_event_id = None
                print("[READY ACK] Backend recorded current QR", current_marker)
            else:
                print("[READY REJECTED]", data.get("reason", "Unknown reason"))
                invalidate_localization("Backend rejected localization; refresh map and camera")
                ready_acknowledged = False
                ready_event_id = None
                ready_last_sent = time.monotonic()  # avoid spamming rejected READY
        elif topic in (STATUS_ACK_TOPIC, QR_PROGRESS_ACK_TOPIC):
            if pending is not None and topic == pending["ack_topic"] and \
                    data.get("eventId") == pending["eventId"]:
                pending["accepted"] = data.get("accepted") is True
                pending["reason"] = str(data.get("action", "STOP") if pending["kind"] == "VERIFICATION_STATUS"
                                        else data.get("reason", data.get("result", "")))


def tick_pending():
    global pending
    if pending is None:
        return None
    if pending["accepted"] is not None:
        outcome = (pending["accepted"], pending["reason"], pending["kind"], pending["index"])
        pending = None
        return outcome
    now = time.monotonic()
    if now >= pending["deadline"]:
        outcome = (False, "Timed out waiting for Backend application ACK", pending["kind"], pending["index"])
        pending = None
        return outcome
    if now >= pending["retry_at"]:
        if connected:
            publish(pending["topic"], pending["body"])
        pending["retry_at"] = now + ACK_RETRY_SECONDS
    return None


# SF_INTERSECTION_PILOT_V1: fail-closed Webots intersection reservation.
# This pilot uses simulated CLEAR sensor messages only when explicitly enabled.
# Real physical robots MUST use actual obstacle sensing and safety-rated controls.

def sf_timestamp(raw):
    if not isinstance(raw, str):
        return None
    try:
        # .NET emits up to seven fractional digits. Python 3.10 fromisoformat
        # accepts only three or six; normalize explicitly without extending TTL.
        import re
        match = re.fullmatch(r'(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})', raw)
        if match is None:
            return None
        fraction = '.' + match[2][:6].ljust(6, '0') if match[2] else ''
        value = sf_datetime.datetime.fromisoformat(match[1] + fraction + match[3].replace('Z', '+00:00'))
        if value.tzinfo is None:
            return None
        return value.astimezone(sf_datetime.timezone.utc)
    except (ValueError, TypeError):
        return None


def sf_accept_nav_directive(data):
    global sf_nav_epoch, sf_nav_sequence, sf_nav_directive, sf_nav_notice
    if not INTERSECTION_PILOT or data.get('robotCode') != ROBOT_CODE:
        return
    epoch, seq = data.get('epoch'), data.get('sequence')
    expiry = sf_timestamp(data.get('expiresAtUtc'))
    if not isinstance(epoch, str) or not epoch or type(seq) is not int or expiry is None:
        sf_nav_directive = None
        stop_robot()
        return
    if sf_nav_epoch is not None and sf_nav_epoch != epoch:
        stop_robot()
        sf_nav_directive = None
        # Coordinator restart may have lost transient state; never resume motion
        # using a GO from the new epoch without manual recovery.
        if mission is not None:
            print('[TRAFFIC {}] BACKEND EPOCH CHANGED: halting, check intersection lock'.format(ROBOT_CODE))
            fail_mission('Navigation Backend restarted during mission; manual recovery required',
                         notify_backend=True)
            return
        sf_nav_sequence = -1
    if sf_nav_epoch == epoch and seq <= sf_nav_sequence:
        return
    sf_nav_epoch, sf_nav_sequence = epoch, seq
    if expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        sf_nav_directive = None
        stop_robot()
        return
    if data.get('mode') not in ('STOP', 'SLOW', 'GO'):
        sf_nav_directive = None
        stop_robot()
        return
    sf_nav_directive = dict(data)
    sf_nav_directive['_expiry'] = expiry
    notice = (data.get('mode'), data.get('intersectionArUcoId'),
              data.get('intersectionGranted'), data.get('reason'))
    if notice != sf_nav_notice:
        print('[TRAFFIC {}] NAV {} intersection={} granted={} reason={}'.format(
            ROBOT_CODE, *notice))
        sf_nav_notice = notice
    if data.get('mode') == 'STOP':
        stop_robot()


def sf_accept_nav_ack(data):
    if not INTERSECTION_PILOT or sf_nav_request is None:
        return
    if data.get('robotCode') != ROBOT_CODE or data.get('eventId') != sf_nav_request['eventId']:
        return
    if data.get('kind') != sf_nav_request['kind']:
        return
    sf_nav_request['accepted'] = data.get('accepted') is True
    sf_nav_request['reason'] = str(data.get('reason', ''))


def sf_validate_intersection_waypoints(waypoints, allow_stationary=False):
    if not isinstance(waypoints, list) or len(waypoints) < (1 if allow_stationary else 2):
        raise ValueError('Traffic pilot requires at least two route checkpoints')
    for w in waypoints:
        if type(w.get('isIntersection')) is not bool:
            raise ValueError('Backend route missing isIntersection; install Backend pilot patch first')
    if waypoints[0]['isIntersection'] or waypoints[-1]['isIntersection']:
        raise ValueError('Traffic pilot route must start and finish OUTSIDE intersections')
    if any(a['isIntersection'] and b['isIntersection'] for a, b in zip(waypoints, waypoints[1:])):
        raise ValueError('Adjacent intersections unsupported in safe pilot')


def sf_nav_send(kind, data, payload, intersection_id=None):
    global sf_nav_request
    if sf_nav_request is not None:
        raise ValueError('Navigation request still pending')
    event_id = str(uuid.uuid4())
    payload = dict(payload)
    payload['eventId'] = event_id
    topic = SF_SCAN_TOPIC if kind == 'SCAN' else SF_NAV_EVENT_TOPIC
    sf_nav_request = {
        'kind': kind, 'eventId': event_id, 'topic': topic, 'payload': payload,
        'intersectionId': intersection_id, 'accepted': None, 'reason': '',
        'deadline': time.monotonic() + SF_NAV_ACK_TIMEOUT,
        'retry_at': time.monotonic() + SF_NAV_ACK_RETRY
    }
    data['phase'] = 'NAV_WAIT_ACK'
    stop_robot()
    if not publish(topic, payload):
        print('[TRAFFIC {}] Waiting for MQTT to send {}'.format(ROBOT_CODE, kind))


def sf_begin_segment(data):
    """Stop at predecessor checkpoint and register before any next movement."""
    if not INTERSECTION_PILOT:
        return sf_start_physical_segment(data)
    i = data['next_index']
    if i < 1 or i >= len(data['waypoints']):
        raise ValueError('Invalid nav waypoint index')
    prev, target = data['waypoints'][i - 1], data['waypoints'][i]
    if current_marker != prev['arucoId']:
        raise ValueError('Camera QR does not match predecessor stop checkpoint')
    if sf_active_intersection is not None:
        if prev['arucoId'] != sf_active_intersection or target['isIntersection']:
            raise ValueError('Unexpected active intersection reservation on next segment')
        data['phase'] = 'NAV_WAIT_GRANT'
        data['sf_hold_until'] = time.monotonic() + SF_TEST_HOLD_SECONDS
        if SF_TEST_HOLD_SECONDS:
            print('[TRAFFIC {}] TEST HOLD for {:.1f}s inside QR{} (Webots-only)'.format(
                ROBOT_CODE, SF_TEST_HOLD_SECONDS, sf_active_intersection))
        print('[TRAFFIC {}] Keep QR{} reserved while driving toward exit QR{}'.format(
            ROBOT_CODE, sf_active_intersection, target['arucoId']))
        stop_robot()
        return
    if target['isIntersection']:
        kind = 'APPROACH'
        payload = {'event': 'APPROACH', 'intersectionArUcoId': target['arucoId'],
                   'currentArUcoId': current_marker}
        print('[TRAFFIC {}] APPROACH QR{} from QR{}; stopped for Backend grant'.format(
            ROBOT_CODE, target['arucoId'], current_marker))
        sf_nav_send(kind, data, payload, target['arucoId'])
    else:
        print('[TRAFFIC {}] Register corridor QR{} -> QR{}'.format(
            ROBOT_CODE, current_marker, target['arucoId']))
        sf_nav_send('SCAN', data, {'currentArUcoId': current_marker,
                                   'nextArUcoId': target['arucoId'],
                                   'followingArUcoId': data['waypoints'][i+1]['arucoId'] if i+1 < len(data['waypoints']) else None})


def sf_move_permission(data):
    """Fresh Backend directive is mandatory for *every* moving segment."""
    if SF_REAL_SIM_LIDAR and sf_local_obstacle in ('CRITICAL','NO_SCAN_DATA'):
        return False
    directive = sf_nav_directive
    if not connected or directive is None:
        return False
    if directive.get('mode') not in ('GO', 'SLOW'):
        return False
    expiry = directive.get('_expiry')
    if expiry is None or expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        return False
    try:
        speed = float(directive.get('maxLinearSpeed', 0))
        if not 0 < speed <= 5:
            return False
    except (ValueError, TypeError):
        return False
    i = data.get('next_index', 0)
    if i < 1 or i >= len(data['waypoints']):
        return False
    target = data['waypoints'][i]
    protected = target['arucoId'] if target['isIntersection'] else sf_active_intersection
    if protected is not None:
        if (directive.get('intersectionArUcoId') != protected or
                directive.get('intersectionGranted') is not True or
                directive.get('mode') != 'GO'):
            return False
    else:
        if directive.get('intersectionArUcoId') is not None:
            return False
    return True


def sf_tick_nav_gate(data):
    global sf_nav_request, sf_active_intersection, sf_last_hold_log
    stop_robot()
    if data['phase'] == 'NAV_WAIT_ACK':
        req = sf_nav_request
        if req is None:
            fail_mission('Missing navigation request state')
            return
        if req['accepted'] is False:
            fail_mission('Backend rejected navigation {}: {}'.format(req['kind'], req['reason']))
            return
        if req['accepted'] is None:
            now = time.monotonic()
            if now >= req['deadline']:
                fail_mission('No Backend ACK for navigation {}'.format(req['kind']))
                return
            if connected and now >= req['retry_at']:
                publish(req['topic'], req['payload'])
                req['retry_at'] = now + SF_NAV_ACK_RETRY
            return
        kind, protected = req['kind'], req['intersectionId']
        sf_nav_request = None
        if kind in ('SCAN', 'APPROACH'):
            data['phase'] = 'NAV_WAIT_GRANT'
            return
        if kind == 'ENTER':
            sf_active_intersection = protected
            print('[TRAFFIC {}] ENTER QR{} acknowledged; reservation held'.format(
                ROBOT_CODE, protected))
            sf_start_physical_segment(data)
            return
        if kind == 'EXIT':
            print('[TRAFFIC {}] EXIT QR{} acknowledged at QR{}; lock released'.format(
                ROBOT_CODE, sf_active_intersection, current_marker))
            sf_active_intersection = None
            if data.get('home_command_id'):
                data['next_index'] += 1
                if data['next_index'] >= len(data['waypoints']):
                    finish_home(data)
                else:
                    sf_begin_segment(data)
            else:
                send_progress(data, data['next_index'])
                data['phase'] = 'WAIT_WAYPOINT_ACK'
            return
        fail_mission('Unexpected navigation ack kind: ' + kind)
        return
    if data['phase'] == 'NAV_WAIT_GRANT':
        # Refresh the stopped robot's route while another robot owns the intersection.
        # An ACK retry reuses an event ID and is deduplicated by the backend, so
        # each fresh route report needs its own ID. Never clear an entered lock.
        now = time.monotonic()
        if sf_active_intersection is None and now >= data.get('sf_route_refresh_at', now + 1):
            target = data['waypoints'][data['next_index']]
            data['sf_route_refresh_at'] = now + 2.0
            if target['isIntersection']:
                sf_nav_send('APPROACH', data, {'event': 'APPROACH',
                    'intersectionArUcoId': target['arucoId'], 'currentArUcoId': current_marker}, target['arucoId'])
            else:
                sf_nav_send('SCAN', data, {'currentArUcoId': current_marker,
                    'nextArUcoId': target['arucoId'],
                    'followingArUcoId': data['waypoints'][data['next_index']+1]['arucoId'] if data['next_index']+1 < len(data['waypoints']) else None})
            return
        data.setdefault('sf_route_refresh_at', now + 2.0)
        if time.monotonic() < data.get('sf_hold_until', 0):
            return
        if not sf_move_permission(data):
            now = time.monotonic()
            if now - sf_last_hold_log >= 5:
                target = data['waypoints'][data['next_index']]['arucoId']
                print('[TRAFFIC {}] HOLD at QR{}: waiting valid GO for QR{}'.format(
                    ROBOT_CODE, current_marker, target))
                sf_last_hold_log = now
            return
        next_wp = data['waypoints'][data['next_index']]
        if next_wp['isIntersection']:
            intersection_id = next_wp['arucoId']
            print('[TRAFFIC {}] GRANTED QR{}; confirm protected entry'.format(
                ROBOT_CODE, intersection_id))
            sf_nav_send('ENTER', data, {'event': 'ENTER',
                                       'intersectionArUcoId': intersection_id}, intersection_id)
        else:
            sf_start_physical_segment(data)
        return
    fail_mission('Invalid traffic gate phase ' + str(data['phase']))


def sf_tick_lidar():
    """Webots laser safety, with the legacy fake-clear pilot kept opt-in."""
    global sf_last_lidar_at, sf_local_obstacle
    report = {'status':'CLEAR','distanceCm':None}
    if SF_REAL_SIM_LIDAR:
        report = {'status':'NO_SCAN_DATA','distanceCm':None}
        if sim_lidar is not None:
            report=front_lidar_report(sim_lidar.getRangeImage(),sim_lidar.getFov())
        sf_local_obstacle=report['status']
        if sf_local_obstacle in ('CRITICAL','NO_SCAN_DATA'): stop_robot()
    if not INTERSECTION_PILOT or not connected or (not SF_FAKE_CLEAR and not SF_REAL_SIM_LIDAR):
        return
    now = time.monotonic()
    if now - sf_last_lidar_at < SF_LIDAR_INTERVAL:
        return
    sf_last_lidar_at = now
    publish(SF_LIDAR_TOPIC, {**report,
                             'simulated': True, 'source': 'WEBOTS_LASER' if SF_REAL_SIM_LIDAR else 'WEBOTS_PILOT_ONLY'}, qos=1)


def sf_wheel_scale():
    """Apply NAV_CONTROL's speed as a conservative *relative* wheel cap.

    BE maxLinearSpeed is a ROS-style command, not calibrated wheel rad/s.
    The 0.18 reference only maps demo speeds; it is not a real m/s claim.
    """
    if not INTERSECTION_PILOT:
        return 1.0
    if sf_nav_directive is None or sf_nav_directive.get('mode') == 'STOP':
        return 0.0
    expiry = sf_nav_directive.get('_expiry')
    if expiry is None or expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        return 0.0
    try:
        cap = float(sf_nav_directive.get('maxLinearSpeed', 0))
        return max(0.0, min(1.0, cap / 0.18))
    except (ValueError, TypeError):
        return 0.0


def begin_segment(data):
    return sf_begin_segment(data)


def sf_start_physical_segment(data):
    global current_state, state_start_time, turn_direction, stable_counter
    global last_detected_id, missing_align_frames, mission_camera_lost_since
    global approach_seen, approach_near, approach_lost_frames
    global approach_max_size, approach_last_band
    i = data["next_index"]
    target_id = data["waypoints"][i]["arucoId"]
    if i <= 0 or current_marker != data["waypoints"][i - 1]["arucoId"]:
        raise ValueError("Camera position does not match preceding Backend waypoint")
    # Backend computed travelDirection from the saved map's linked QR locations.
    turn_angle, turn_dir = decide_turn(data["waypoints"][i]["travelDirection"])
    stable_counter = 0
    last_detected_id = -1
    missing_align_frames = 0
    mission_camera_lost_since = None
    approach_seen = False
    approach_near = False
    approach_lost_frames = 0
    approach_max_size = 0.0
    approach_last_band = None
    data["segment_started_at"] = robot.getTime()
    data['encoder_origin'] = (left_encoder.getValue(),right_encoder.getValue())
    prev_wp = data['waypoints'][i-1]
    next_wp = data['waypoints'][i]
    data['segment_distance_m'] = (abs(prev_wp['row']-next_wp['row'])+abs(prev_wp['col']-next_wp['col']))*.5
    if turn_angle == 0.0:
        current_state = STATE_ALIGNING
        print("[DRIVE] QR{} -> QR{} (straight/align)".format(current_marker, target_id))
    else:
        turn_direction = turn_dir
        state_start_time = robot.getTime()
        current_state = STATE_OVERSHOOT
        print("[DRIVE] QR{} -> QR{} (turn {} deg {})".format(
            current_marker, target_id, round(turn_angle, 1), turn_dir))
    data["phase"] = "MOVING"


def reached_marker(data, detected_id):
    global current_marker, current_state, stable_counter, last_detected_id
    stop_robot()
    i = data["next_index"]
    if detected_id != data["waypoints"][i]["arucoId"]:
        raise ValueError("Unexpected QR arrival")
    if i > 0:
        try:
            store_heading_calibration(detected_id)
        except (ValueError, cv2.error, TypeError) as error:
            # A rejected image is neither a reached waypoint nor an intersection EXIT.
            # Stay stopped at the arrival zone and allow a bounded camera retry.
            current_state = STATE_ALIGNING
            stable_counter = 0
            since = data.setdefault("heading_confirmation_since", time.monotonic())
            if time.monotonic() - since >= 5.0:
                fail_mission("Heading verification failed after stopped retries: " + str(error))
            return
    data.pop("heading_confirmation_since", None)
    current_marker = detected_id
    current_state = STATE_IDLE
    stable_counter = 0
    last_detected_id = -1
    if INTERSECTION_PILOT and sf_active_intersection is not None and i > 0 and             data["waypoints"][i - 1]["arucoId"] == sf_active_intersection:
        if data["waypoints"][i].get("isIntersection"):
            raise ValueError("Cannot exit directly into a second intersection")
        print("[TRAFFIC {}] QR{} camera-confirmed beyond intersection QR{}; request EXIT".format(
            ROBOT_CODE, current_marker, sf_active_intersection))
        sf_nav_send("EXIT", data, {"event": "EXIT", "intersectionArUcoId": sf_active_intersection,
                                   "currentArUcoId": current_marker}, sf_active_intersection)
        return
    if data.get("home_command_id"):
        data["next_index"] = i + 1
        if data["next_index"] >= len(data["waypoints"]):
            finish_home(data)
        else:
            begin_segment(data)
        return
    if data.get("chargingJobId"):
        send_charging_progress(data, i)
    else:
        send_progress(data, i)
    data["phase"] = "WAIT_WAYPOINT_ACK"


def tick_mission():
    global mission, current_state, stable_counter, last_detected_id
    global missing_align_frames, target_yaw_deg, ready_acknowledged
    global ready_event_id, ready_last_sent, safety_hold
    if safety_hold and (mission is None or mission.get("phase") != "HOME_ACK"):
        stop_robot()
        return
    if mission is None:
        stop_robot()
        return
    data = mission
    if INTERSECTION_PILOT and sf_reconnected_with_mission:
        stop_robot()
        fail_mission("MQTT connection lost during reserved navigation; manual recovery required",
                     notify_backend=True)
        return
    if not connected:
        stop_robot()  # fail-closed on MQTT loss; no blind motion
        return
    result = tick_pending()
    if result is not None:
        accepted, reason, kind, index = result
        if not accepted:
            # Rejection/timeout of completion is a control-plane failure AFTER
            # physical arrival. Never falsely report a navigation MISSION_FAILED.
            if kind == "MISSION_COMPLETED":
                fail_mission("Backend did not accept completed delivery: {}".format(reason),
                             notify_backend=False)
            elif kind == "MISSION_STARTED":
                fail_mission("Backend did not accept mission start: {}".format(reason),
                             notify_backend=False)
            else:
                fail_mission("Backend rejected {}: {}".format(kind, reason))
            return
        if kind == "HOME_REACHED":
            safety_commands[data["home_command_id"]] = ("HOME_REACHED", current_marker)
            safety_hold = False
            if os.path.exists(SAFETY_FILE):
                os.remove(SAFETY_FILE)
            mission = None
            current_state = STATE_IDLE
            stop_robot()
            return
        if kind == "CHARGE_WAYPOINT":
            data["next_index"] = index + 1
            if data["next_index"] == len(data["waypoints"]):
                finish_charging_arrival(data)
            else:
                begin_segment(data)
            return
        if kind == "MISSION_STARTED":
            # The start is a CAMERA-confirmed QR, not an arbitrary numeric pose.
            send_progress(data, 0)
            data["phase"] = "WAIT_WAYPOINT_ACK"
            return
        if kind == "WAYPOINT_REACHED":
            data["next_index"] = index + 1
            if index == data["pickupWaypointIndex"] or index == len(data["waypoints"]) - 1:
                data["phase"] = "WAIT_VERIFICATION"
                data["verification_poll_at"] = 0.0
                stop_robot()
                return
            if data["next_index"] >= len(data["waypoints"]):
                if current_marker != data["deliveryArUcoId"]:
                    fail_mission("Last camera marker is not the delivery QR")
                    return
                send_status("MISSION_COMPLETED", data)
                data["phase"] = "FINISHING"
                return
            try:
                begin_segment(data)
            except ValueError as ex:
                fail_mission(str(ex))
            return
        if kind == "MISSION_COMPLETED":
            print("[SUCCESS] Backend ACKed MISSION_COMPLETED for Mission", data["missionId"])
            mission = None
            current_state = STATE_IDLE
            stop_robot()
            # The final QR_PROGRESS ACK already confirmed the current marker
            # to Backend. Accept the next task immediately, AND refresh READY.
            ready_acknowledged = True
            ready_event_id = None
            ready_last_sent = 0.0
            return
        if kind == "VERIFICATION_STATUS":
            if reason in ("WAIT_PICKUP_VERIFICATION", "WAIT_DELIVERY_VERIFICATION"):
                data["phase"] = "WAIT_VERIFICATION"
                stop_robot()
            elif reason == "CONTINUE" and index == data["pickupWaypointIndex"]:
                try:
                    begin_segment(data)
                except ValueError as ex:
                    fail_mission(str(ex))
            elif reason == "COMPLETE" and index == len(data["waypoints"]) - 1 and current_marker == data["deliveryArUcoId"]:
                send_status("MISSION_COMPLETED", data)
                data["phase"] = "FINISHING"
            else:
                fail_mission("Unexpected verification instruction: " + reason)
            return
    if pending is not None:
        stop_robot()
        return
    if data["phase"] == "WAIT_VERIFICATION":
        stop_robot()
        if time.monotonic() >= data.get("verification_poll_at", 0):
            poll_verification(data)
        return
    if INTERSECTION_PILOT:
        if data["phase"] in ("NAV_WAIT_ACK", "NAV_WAIT_GRANT"):
            sf_tick_nav_gate(data)
            return
        if data["phase"] == "MOVING" and not sf_move_permission(data):
            stop_robot()
            return
    if data["phase"] != "MOVING":
        stop_robot()
        return
    if robot.getTime() - data["segment_started_at"] > MAX_SIM_SECONDS_PER_WAYPOINT:
        fail_mission("Camera did not reach QR{} within {} simulated seconds".format(
            data["waypoints"][data["next_index"]]["arucoId"], MAX_SIM_SECONDS_PER_WAYPOINT))
        return

    target_id = data["waypoints"][data["next_index"]]["arucoId"]
    if data.get('encoder_origin') is not None:
        left,right=data['encoder_origin']
        distance=abs((left_encoder.getValue()-left+right_encoder.getValue()-right)*.033/2)
        if distance > data['segment_distance_m']+.15 and visible_marker(target_id) is None:
            fail_mission('Expected QR{} missing at odometry travel limit; stopped for inspection/relocalization'.format(target_id))
            return
    now = robot.getTime()
    if current_state in (STATE_SEEKING, STATE_ALIGNING):
        visual_approach(data, target_id)

    elif current_state == STATE_OVERSHOOT:
        if now - state_start_time < OVERSHOOT_TIME:
            drive_straight_with_imu(MAX_SPEED)
        else:
            current_state = STATE_TURNING
            target_yaw_deg = get_absolute_target_yaw()

    elif current_state == STATE_TURNING:
        current_yaw = get_yaw_deg()
        heading_error = normalize_angle(target_yaw_deg - current_yaw)
        if abs(heading_error) > 1.5:
            speed = TURN_SPEED if abs(heading_error) > SLOWDOWN_THRESHOLD else max(
                TURN_SPEED * (abs(heading_error) / SLOWDOWN_THRESHOLD), MIN_TURN_SPEED)
            if heading_error < 0:
                set_motor_speeds(speed, -speed)
            else:
                set_motor_speeds(-speed, speed)
        else:
            print("[TURN] Complete. IMU yaw=", round(current_yaw, 1))
            stop_robot()
            current_state = STATE_SEEKING
            stable_counter = 0
            last_detected_id = -1
    else:
        fail_mission("Invalid movement state: " + str(current_state))


def invalidate_localization(reason):
    global localization_valid, is_first_checkin, current_marker
    global ready_acknowledged, ready_event_id, localization_reason
    if localization_valid:
        publish(QR_PROGRESS_TOPIC, {"event": "LOCALIZATION_LOST", "eventId": str(uuid.uuid4()),
                                   "arucoId": current_marker or 0, "missionId": 0})
    localization_valid = False
    is_first_checkin = True
    current_marker = None
    ready_acknowledged = False
    ready_event_id = None
    localization_reason = reason
    heading_fusion.reset()
    stop_robot()


def accept_localization_map(data):
    global localization_map, localization_request_id
    if data.get("eventId") != localization_request_id:
        return
    snapshot = data.get("map")
    if not isinstance(snapshot, dict) or snapshot.get("protocolVersion") != 1 or snapshot.get("frame") != "MAP_N_W_UP":
        return
    if not valid_int(snapshot.get("mapRevision")) or not isinstance(snapshot.get("markers"), list):
        return
    ids = [m.get("arucoId") for m in snapshot["markers"] if isinstance(m, dict)]
    if len(ids) != len(snapshot["markers"]) or len(set(ids)) != len(ids) or any(not valid_int(i) for i in ids):
        return
    if localization_map is None or snapshot["mapRevision"] != localization_map["mapRevision"]:
        if mission is not None:
            fail_mission("Backend map changed during a mission; relocalization required")
        invalidate_localization("Backend localization map updated")
        print("[LOCALIZATION MAP] revision {} | {} markers".format(snapshot["mapRevision"], len(ids)))
    localization_map = snapshot
    localization_request_id = None


def request_localization_map():
    global localization_request_id, localization_request_at
    if not connected or mission is not None or time.monotonic() - localization_request_at < 4.0:
        return
    localization_request_id = str(uuid.uuid4())
    if publish(QR_PROGRESS_TOPIC, {"event": "LOCALIZATION_CONFIG", "eventId": localization_request_id,
                                 "arucoId": 0, "missionId": 0}):
        localization_request_at = time.monotonic()


def tick_localization(force=False):
    global localization_valid, localization_last_frame_at, localization_last_warning_at
    global localization_reconnect, current_marker, current_heading, base_yaw_offset, is_first_checkin
    global ready_acknowledged, ready_event_id, ready_last_sent, localization_reason
    if mission is not None:
        return
    stop_robot()
    if localization_reconnect:
        invalidate_localization("MQTT reconnected; checking camera pose again")
        localization_reconnect = False
    request_localization_map()
    if not force and robot.getTime() - localization_last_frame_at < 0.20:
        return
    localization_last_frame_at = robot.getTime()
    try:
        if not connected or localization_map is None:
            raise ValueError("Waiting for current marker calibration from Backend")
        if pose_estimator is None:
            raise ValueError("Camera installation calibration is missing or invalid")
        seen = visible_any_marker()
        if seen is None:
            raise ValueError("No camera-visible ArUco marker; hold position")
        marker_id, corners, _ = seen
        marker = next((m for m in localization_map["markers"] if m["arucoId"] == marker_id), None)
        if marker is None:
            raise ValueError("Detected QR{} is absent from the current map".format(marker_id))
        pose = pose_estimator.estimate(corners[0], marker)
        stable = heading_fusion.observe(marker_id, pose["headingDeg"], get_yaw_deg(), pose["reprojectionErrorPx"])
        if not stable:
            if localization_valid:
                invalidate_localization("Marker/heading changed; collecting stable camera frames")
            return
        first = not localization_valid
        current_marker = marker_id
        base_yaw_offset = heading_fusion.offset
        current_heading = min(DIRECTION_YAW, key=lambda d: abs(normalize_angle(pose["headingDeg"] - DIRECTION_YAW[d])))
        localization_valid = True
        is_first_checkin = False
        localization_reason = "ARUCO_IMU"
        if first:
            ready_acknowledged = False
            ready_event_id = None
            ready_last_sent = 0.0
            print("[AUTO LOCALIZED] QR{} | heading={:.1f} deg ({}) | IMU offset={:.1f} | map={} | error={:.2f}px".format(
                marker_id, heading_fusion.heading(get_yaw_deg()), current_heading, base_yaw_offset,
                localization_map["mapRevision"], pose["reprojectionErrorPx"]))
            send_ready()
        if localization_diagnostics_dir:
            os.makedirs(localization_diagnostics_dir, exist_ok=True)
            with open(os.path.join(localization_diagnostics_dir, ROBOT_CODE + ".json"), "w", encoding="utf-8") as handle:
                json.dump({"qr": marker_id, "headingDeg": heading_fusion.heading(get_yaw_deg()),
                           "imuYawDeg": get_yaw_deg(), "mapRevision": localization_map["mapRevision"],
                           "errorPx": pose["reprojectionErrorPx"], "stableFrames": len(heading_fusion.samples)}, handle)
    except (ValueError, cv2.error, KeyError, TypeError) as error:
        invalidate_localization(str(error))
        if time.monotonic() - localization_last_warning_at >= 5.0:
            print("[LOCALIZATION HOLD]", error)
            if localization_diagnostics_dir:
                os.makedirs(localization_diagnostics_dir, exist_ok=True)
                with open(os.path.join(localization_diagnostics_dir, ROBOT_CODE + ".hold.json"), "w") as handle:
                    json.dump({"reason": str(error), "imuYawDeg": get_yaw_deg()}, handle)
                frame = get_camera_frame()
                if frame is not None:
                    cv2.imwrite(os.path.join(localization_diagnostics_dir, ROBOT_CODE + ".camera.png"), frame)
            localization_last_warning_at = time.monotonic()


def tick_bootstrap():
    tick_localization()



def tick_telemetry():
    global last_telemetry_at
    # Presence deadlines are wall-clock deadlines. Slow rendering must not turn
    # two simulated seconds into a minute between network heartbeats. A frozen
    # robot.step still cannot publish, so genuine simulator stalls remain faults.
    if not connected or time.monotonic() - last_telemetry_at < 2.0:
        return
    last_telemetry_at = time.monotonic()
    # Webots-only dummy battery; do not fabricate physical location from a
    # camera that currently only reports discrete ArUco checkpoints.
    publish(TELEMETRY_TOPIC, {
        "batteryPercent": SIMULATED_BATTERY_PERCENT,
        "batteryVoltage": SIMULATED_BATTERY_VOLTAGE,
        "heading": round(get_yaw_deg(), 2),
        "localizationStatus": "LOCALIZED" if localization_valid else "LOCALIZING",
        "localizationReason": localization_reason,
    }, qos=0)


print("[BOOT] SmartFleet Webots CAMERA ArUco full-mission driver", ROBOT_CODE)
print("[BOOT] Automatic camera pose + IMU localization; no start pose arguments")
print("[BOOT] Cruise scale {:.2f}; approach, QR arrival and turn speeds unchanged".format(CRUISE_SCALE))
print("[BOOT] Waiting for a camera-visible QR and Backend READY ACK")
print("[BOOT] MANUAL 0-8 keyboard mission controls are disabled; Q stops the pilot")
if INTERSECTION_PILOT:
    print("[TRAFFIC] INTERSECTION PILOT ON for", ROBOT_CODE,
          "| SIMULATED CLEAR (Webots only) =", SF_FAKE_CLEAR)
    if not SF_FAKE_CLEAR:
        print("[TRAFFIC] Without fresh real sensor data the Backend must keep the robot stopped")
try:
    client.connect(BROKER_IP, BROKER_PORT, 60)
    client.loop_start()
    diagnostic_at = 0.0
    while robot.step(timestep) != -1 and is_running:
        handle_incoming()
        tick_localization()
        tick_telemetry()
        sf_tick_lidar()
        tick_ready()
        tick_charging()
        key = keyboard.getKey()
        if key != -1 and (key & 0xFF) in (ord("q"), ord("Q")):
            print("[STOP] Emergency keyboard Q")
            if mission is not None:
                fail_mission("Operator pressed Q in Webots")
            is_running = False
            break
        if is_first_checkin:
            tick_bootstrap()
        else:
            tick_mission()
        if localization_diagnostics_dir and time.monotonic() - diagnostic_at >= 1:
            diagnostic_at = time.monotonic()
            os.makedirs(localization_diagnostics_dir, exist_ok=True)
            diagnostic = {"phase": mission.get("phase") if mission else None, "state": current_state,
                "missionId": mission.get("missionId") if mission else None,
                "permission": sf_move_permission(mission) if mission else False,
                "leftCommand": left_motor.getVelocity(), "rightCommand": right_motor.getVelocity(),
                "yaw": get_yaw_deg(), "targetYaw": get_absolute_target_yaw(),
                "navMode": sf_nav_directive.get("mode") if sf_nav_directive else None,
                "navReason": sf_nav_directive.get("reason") if sf_nav_directive else None,
                "pending": pending.get("kind") if pending else None, "safetyHold": bool(safety_hold)}
            with open(os.path.join(localization_diagnostics_dir, ROBOT_CODE + ".motion.json"), "w") as handle:
                json.dump(diagnostic, handle)
            frame = get_camera_frame()
            if frame is not None:
                cv2.imwrite(os.path.join(localization_diagnostics_dir, ROBOT_CODE + ".motion.png"), frame)
except KeyboardInterrupt:
    print("[STOP] KeyboardInterrupt")
except Exception as error:
    import traceback
    traceback.print_exc()
    print("[FATAL]", repr(error))
    if mission is not None:
        fail_mission("Webots controller exception: " + str(error))
finally:
    stop_robot()
    try:
        client.loop_stop()
        client.disconnect()
    except Exception:
        pass
    print("[STOP] Webots controller stopped")
