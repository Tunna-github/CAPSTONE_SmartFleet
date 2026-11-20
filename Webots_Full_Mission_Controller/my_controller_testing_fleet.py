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
import json
import queue
import time
import uuid
import paho.mqtt.client as mqtt
from controller import Robot, Keyboard

# ============================================================
# TEST CONFIGURATION - verify before a mission
# ============================================================
BROKER_IP = "localhost"
BROKER_PORT = 1883
ROBOT_CODE = "R01"            # Must equal the robot assigned in SmartFleet
# QR0 is only a ONE-TIME first-run calibration anchor, not a fixed mission start.
# After the first valid driven segment, the controller learns its world yaw frame
# and can cold-start from ANY visible QR using the saved orientation calibration.
FIRST_RUN_CALIBRATION_QR = 0
FIRST_RUN_HEADING = "S"  # Preserve the proven QR0 initial drive/turn procedure
HEADING_CALIBRATION_FILE = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "smartfleet_heading_calibration_{}.json".format(ROBOT_CODE),
)
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
QR_PROGRESS_ACK_TOPIC = "smartfleet/robot/{}/qr-progress-ack".format(ROBOT_CODE)
TELEMETRY_TOPIC = "smartfleet/robot/{}/telemetry".format(ROBOT_CODE)

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

imu = robot.getDevice("inertial unit")
imu.enable(timestep)

aruco_dict = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
aruco_params = cv2.aruco.DetectorParameters()
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
heading_calibration = None
last_bootstrap_warning_at = 0.0


def set_motor_speeds(v_left, v_right):
    """Working wheel control, capped at ROBOT_MAX_VEL."""
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


def decide_turn(direction_to_go):
    """Unchanged steering math; the requested cardinal direction comes from BE."""
    global current_heading
    if direction_to_go not in DIRECTION_YAW:
        raise ValueError("Invalid Backend travelDirection " + repr(direction_to_go))
    current_yaw_target = DIRECTION_YAW[current_heading]
    new_yaw_target = DIRECTION_YAW[direction_to_go]
    angle_diff = normalize_angle(new_yaw_target - current_yaw_target)
    current_heading = direction_to_go
    if abs(angle_diff) < 1.0:
        return 0.0, None
    if angle_diff > 0:
        return angle_diff, "LEFT"
    return abs(angle_diff), "RIGHT"


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
        if 0 <= marker_id <= 49:
            area = abs(cv2.contourArea(np.asarray(corners[i][0], dtype=np.float32)))
            choices.append((area, marker_id, [corners[i]]))
    if not choices:
        return None
    _, marker_id, selected = max(choices, key=lambda item: item[0])
    return marker_id, selected, camera.getWidth()


def read_heading_calibration():
    try:
        with open(HEADING_CALIBRATION_FILE, "r", encoding="utf-8") as handle:
            value = json.load(handle)
        if value.get("robotCode") != ROBOT_CODE:
            raise ValueError("Calibration belongs to another robot")
        offset = float(value["worldNorthYawOffsetDeg"])
        if not math.isfinite(offset) or not -180 <= offset <= 180:
            raise ValueError("Invalid yaw calibration")
        print("[CALIBRATION] Loaded world yaw offset", round(offset, 1))
        return offset
    except FileNotFoundError:
        return None
    except (OSError, ValueError, TypeError, KeyError) as exc:
        print("[CALIBRATION] Could not load previous orientation:", repr(exc))
        return None


def infer_heading_from_imu(offset):
    actual = get_yaw_deg()
    candidates = sorted(
        DIRECTION_YAW,
        key=lambda direction: abs(normalize_angle(
            actual - (DIRECTION_YAW[direction] + offset))),
    )
    best = candidates[0]
    residual = abs(normalize_angle(actual - (DIRECTION_YAW[best] + offset)))
    if residual > 30.0:
        return None
    return best


def store_heading_calibration():
    """Learn the map/world heading only after a camera-confirmed driven edge."""
    global heading_calibration
    heading_calibration = normalize_angle(
        get_yaw_deg() - DIRECTION_YAW[current_heading])
    data = {"robotCode": ROBOT_CODE,
            "worldNorthYawOffsetDeg": heading_calibration}
    tmp = HEADING_CALIBRATION_FILE + ".tmp"
    try:
        with open(tmp, "w", encoding="utf-8") as handle:
            json.dump(data, handle)
        os.replace(tmp, HEADING_CALIBRATION_FILE)
        print("[CALIBRATION] Learned yaw frame from QR route:",
              round(heading_calibration, 1), "degrees")
    except OSError as exc:
        # The current mission still retains the heading in RAM.
        print("[CALIBRATION] Could not persist orientation:", repr(exc))


def validate_command(data):
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
    connected = reason_code == 0
    if connected:
        cli.subscribe([(COMMAND_TOPIC, 1), (STATUS_ACK_TOPIC, 1),
                       (QR_PROGRESS_ACK_TOPIC, 1)])
        # A reconnect is not proof that the Backend retained an in-memory READY.
        ready_acknowledged = False
        ready_event_id = None
        ready_last_sent = 0.0
        print("[MQTT] Connected for", ROBOT_CODE)
    else:
        print("[MQTT] Connection failed:", reason_code)


def on_disconnect(cli, userdata, disconnect_flags, reason_code, properties=None):
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
    if not connected or is_first_checkin or current_marker is None or mission is not None:
        return
    if ready_event_id is None:
        ready_event_id = str(uuid.uuid4())
    if publish(QR_PROGRESS_TOPIC, {
        "event": "READY", "eventId": ready_event_id,
        "missionId": 0, "arucoId": current_marker, "waypointIndex": 0,
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
    }, QR_PROGRESS_ACK_TOPIC, index=index)


def publish_failure(data, reason):
    if connected:
        publish(STATUS_TOPIC, {
            "eventId": str(uuid.uuid4()), "event": "MISSION_FAILED",
            "taskId": data["taskId"], "assignmentId": data["assignmentId"],
            "missionId": data["missionId"], "reason": str(reason)[:450],
        })


def fail_mission(reason, notify_backend=True):
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
    if mission is not None:
        if data.get("missionId") == mission.get("missionId"):
            print("[MISSION] Duplicate command ignored")
        else:
            print("[MISSION] Busy; another mission command ignored")
        return
    if is_first_checkin or not ready_acknowledged:
        raise ValueError("Robot has not yet camera-detected START QR / READY was not ACKed")
    validate_command(data)
    mission = dict(data)
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


def handle_incoming():
    global ready_acknowledged, ready_event_id, ready_last_sent, pending
    while True:
        try:
            topic, data = incoming.get_nowait()
        except queue.Empty:
            break
        if not isinstance(data, dict):
            continue
        if topic == COMMAND_TOPIC:
            if data.get("command") != "EXECUTE_TASK":
                continue
            try:
                start_mission(data)
            except (ValueError, RuntimeError) as ex:
                print("[COMMAND REJECTED]", ex)
                if mission is None and all(valid_int(data.get(k)) for k in
                                           ("taskId", "assignmentId", "missionId")):
                    publish_failure(data, "Rejected route: " + str(ex))
        elif topic == QR_PROGRESS_ACK_TOPIC and ready_event_id is not None and \
                data.get("eventId") == ready_event_id:
            if data.get("accepted") is True:
                ready_acknowledged = True
                ready_event_id = None
                print("[READY ACK] Backend recorded current QR", current_marker)
            else:
                print("[READY REJECTED]", data.get("reason", "Unknown reason"))
                # No auto resending a rejected start: change BE start configuration.
                ready_acknowledged = False
                ready_event_id = None
                ready_last_sent = time.monotonic()  # avoid spamming rejected READY
        elif topic in (STATUS_ACK_TOPIC, QR_PROGRESS_ACK_TOPIC):
            if pending is not None and topic == pending["ack_topic"] and \
                    data.get("eventId") == pending["eventId"]:
                pending["accepted"] = data.get("accepted") is True
                pending["reason"] = str(data.get("reason", data.get("result", "")))


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


def begin_segment(data):
    global current_state, state_start_time, turn_direction, stable_counter
    global last_detected_id, missing_align_frames, mission_camera_lost_since
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
    data["segment_started_at"] = robot.getTime()
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
    current_marker = detected_id
    current_state = STATE_IDLE
    stable_counter = 0
    last_detected_id = -1
    stop_robot()
    i = data["next_index"]
    if detected_id != data["waypoints"][i]["arucoId"]:
        raise ValueError("Unexpected QR arrival")
    if i > 0:
        # The camera confirmed a real driven edge. Learn the stable yaw frame
        # for future cold-starts at QR2, QR7, or other configured QR markers.
        store_heading_calibration()
    send_progress(data, i)
    data["phase"] = "WAIT_WAYPOINT_ACK"


def tick_mission():
    global mission, current_state, stable_counter, last_detected_id
    global missing_align_frames, target_yaw_deg, ready_acknowledged
    global ready_event_id, ready_last_sent
    if mission is None:
        stop_robot()
        return
    data = mission
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
        if kind == "MISSION_STARTED":
            # The start is a CAMERA-confirmed QR, not an arbitrary numeric pose.
            send_progress(data, 0)
            data["phase"] = "WAIT_WAYPOINT_ACK"
            return
        if kind == "WAYPOINT_REACHED":
            data["next_index"] = index + 1
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
    if pending is not None:
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
    now = robot.getTime()
    if current_state == STATE_SEEKING:
        seen = visible_marker(target_id)
        if seen is not None:
            if last_detected_id == target_id:
                stable_counter += 1
            else:
                stable_counter, last_detected_id = 1, target_id
            if stable_counter >= STABLE_FRAMES:
                reached_marker(data, target_id)
                return
        else:
            stable_counter = 0
            last_detected_id = -1
        drive_straight_with_imu(MAX_SPEED)

    elif current_state == STATE_ALIGNING:
        seen = visible_marker(target_id)
        if seen is not None:
            corners, width = seen
            offset_x, tilt = get_marker_offset_tilt(corners, width)
            correction = KP_ALIGN_OFFSET * offset_x + KP_ALIGN_TILT * tilt
            set_motor_speeds(ALIGN_SPEED - correction, ALIGN_SPEED + correction)
            missing_align_frames = 0
        else:
            missing_align_frames += 1
            if missing_align_frames > 5:
                current_state = STATE_SEEKING
                drive_straight_with_imu(MAX_SPEED)
            else:
                # The previous program kept old wheel commands; after an MQTT
                # ACK we are stopped, so prime a gentle forward movement.
                drive_straight_with_imu(ALIGN_SPEED)

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


def tick_bootstrap():
    """Localize at ANY camera-visible QR; do not assume the robot is at QR0.

    First ever boot can use the known-good QR0 heading calibration. Once a
    driven segment has been confirmed, an automatically learned yaw frame
    enables cold starts from other QR markers. If orientation is unknown,
    the robot STOPS instead of guessing a turn or driving blind.
    """
    global is_first_checkin, current_marker, base_yaw_offset, current_state
    global stable_counter, last_detected_id, current_heading
    global last_bootstrap_warning_at
    stop_robot()
    if not connected:
        return
    seen = visible_any_marker()
    if seen is not None:
        marker_id, corners, width = seen
        if last_detected_id == marker_id:
            stable_counter += 1
        else:
            stable_counter, last_detected_id = 1, marker_id
        if stable_counter >= STABLE_FRAMES:
            actual_yaw = get_yaw_deg()
            if marker_id == FIRST_RUN_CALIBRATION_QR:
                # Preserve the exact proven initial QR0 controller tuning.
                _, tilt = get_marker_offset_tilt(corners, width)
                base_yaw_offset = normalize_angle(actual_yaw - tilt)
                current_heading = FIRST_RUN_HEADING
            elif heading_calibration is not None:
                inferred_heading = infer_heading_from_imu(heading_calibration)
                if inferred_heading is None:
                    if time.monotonic() - last_bootstrap_warning_at >= 5.0:
                        print("[LOCALIZATION] QR{} visible, but IMU orientation is "
                              "between cardinal headings; adjust robot alignment".format(marker_id))
                        last_bootstrap_warning_at = time.monotonic()
                    return
                current_heading = inferred_heading
                base_yaw_offset = heading_calibration
            else:
                if time.monotonic() - last_bootstrap_warning_at >= 5.0:
                    print("[LOCALIZATION] QR{} visible but this controller has no "
                          "orientation calibration yet. First drive from QR{}; "
                          "future restarts can begin here.".format(
                              marker_id, FIRST_RUN_CALIBRATION_QR))
                    last_bootstrap_warning_at = time.monotonic()
                return
            current_marker = marker_id
            is_first_checkin = False
            current_state = STATE_IDLE
            stable_counter = 0
            last_detected_id = -1
            print("[CAMERA] Localized at QR{} (heading {})".format(
                marker_id, current_heading))
            print("[IMU] Grid heading offset = {:.1f} degrees".format(
                base_yaw_offset))
            send_ready()
            return
    else:
        stable_counter = 0
        last_detected_id = -1
        if time.monotonic() - last_bootstrap_warning_at >= 5.0:
            print("[LOCALIZATION] No QR in camera view. Holding motors; "
                  "position the robot over a visible QR to start.")
            last_bootstrap_warning_at = time.monotonic()


def tick_telemetry():
    global last_telemetry_at
    if not connected or robot.getTime() - last_telemetry_at < 2.0:
        return
    last_telemetry_at = robot.getTime()
    # Webots-only dummy battery; do not fabricate physical location from a
    # camera that currently only reports discrete ArUco checkpoints.
    publish(TELEMETRY_TOPIC, {
        "batteryPercent": SIMULATED_BATTERY_PERCENT,
        "batteryVoltage": SIMULATED_BATTERY_VOLTAGE,
        "heading": round(get_yaw_deg(), 2),
    }, qos=0)


print("[BOOT] SmartFleet Webots CAMERA ArUco full-mission driver", ROBOT_CODE)
heading_calibration = read_heading_calibration()
print("[BOOT] Waiting for a camera-visible QR and Backend READY ACK")
print("[BOOT] MANUAL 0-8 keyboard mission controls are disabled; Q stops the pilot")
try:
    client.connect(BROKER_IP, BROKER_PORT, 60)
    client.loop_start()
    while robot.step(timestep) != -1 and is_running:
        handle_incoming()
        tick_telemetry()
        tick_ready()
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
except KeyboardInterrupt:
    print("[STOP] KeyboardInterrupt")
except Exception as error:
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
