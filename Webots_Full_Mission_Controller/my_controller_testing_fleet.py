# SmartFleet Webots full two-leg transport mission (R01).
# Replaces run_circle with backend-planned QR waypoint traversal.
# IMPORTANT: all Webots robot.step() and motor calls are on this one thread.
# Requires TurtleBot3 Burger wheel position sensors for encoder odometry.
# No camera-based ArUco recognition yet: QR arrival is measured from simulated
# wheel encoder pose, calibrated to the Backend map's grid geometry.

import json
import math
import queue
import time
import uuid

import paho.mqtt.client as mqtt
from controller import Robot

# ----- TEST CALIBRATION ------------------------------------------------------
BROKER_IP = "localhost"
BROKER_PORT = 1883
ROBOT_CODE = "R01"
START_ARUCO_ID = 1  # MUST match actual starting QR and BE DemoRoute:RobotStartQr:R01
INITIAL_MAP_HEADING_DEGREES = 0.0  # 0=robot initially points toward increasing MAP columns
WHEEL_RADIUS_M = 0.033
WHEEL_SEPARATION_M = 0.160
MAX_WHEEL_SPEED = 5.5
CRUISE_SPEED_MPS = 0.11
MAX_ANGULAR_RADPS = 1.4
WAYPOINT_TOLERANCE_M = 0.075
MAX_SIM_SECONDS_PER_WAYPOINT = 75.0

COMMAND_TOPIC = "smartfleet/robot/{}/command".format(ROBOT_CODE)
STATUS_TOPIC = "smartfleet/robot/{}/status".format(ROBOT_CODE)
STATUS_ACK_TOPIC = "smartfleet/robot/{}/status-ack".format(ROBOT_CODE)
QR_PROGRESS_TOPIC = "smartfleet/robot/{}/qr-progress".format(ROBOT_CODE)
QR_PROGRESS_ACK_TOPIC = "smartfleet/robot/{}/qr-progress-ack".format(ROBOT_CODE)
TELEMETRY_TOPIC = "smartfleet/robot/{}/telemetry".format(ROBOT_CODE)
SIMULATED_BATTERY_PERCENT = 85.0  # Webots-only test data, not physical Jetson battery
SIMULATED_BATTERY_VOLTAGE = 11.6

robot = Robot()
timestep = int(robot.getBasicTimeStep())
left_motor = robot.getDevice("left wheel motor")
right_motor = robot.getDevice("right wheel motor")
if left_motor is None or right_motor is None:
    raise RuntimeError("TurtleBot3 Burger needs 'left wheel motor' and 'right wheel motor'.")
left_motor.setPosition(float("inf"))
right_motor.setPosition(float("inf"))
left_motor.setVelocity(0)
right_motor.setVelocity(0)


def find_encoder(motor, candidate_names):
    # Works with built-in Motor position sensors or stand-alone Webots devices.
    sensor = None
    try:
        sensor = motor.getPositionSensor()
    except (AttributeError, RuntimeError):
        sensor = None
    if sensor is None:
        for name in candidate_names:
            try:
                sensor = robot.getDevice(name)
            except (RuntimeError, ValueError):
                sensor = None
            if sensor is not None:
                break
    if sensor is not None:
        sensor.enable(timestep)
    return sensor


left_sensor = find_encoder(left_motor, ["left wheel sensor", "left wheel position sensor"])
right_sensor = find_encoder(right_motor, ["right wheel sensor", "right wheel position sensor"])
if left_sensor is None or right_sensor is None:
    raise RuntimeError(
        "Cannot read wheel encoders. In your Webots TurtleBot3 robot, add/enable "
        "PositionSensor under both wheel motors. This controller refuses to "
        "claim QR arrival from a timer or blind wheel commands."
    )

# Pose is in a coordinate frame aligned with the virtual QR map's rows/columns.
pose_x = 0.0
pose_y = 0.0  # map row direction, NOT Webots vertical axis
pose_heading = math.radians(INITIAL_MAP_HEADING_DEGREES)
prev_left = None
prev_right = None
last_reached_qr = START_ARUCO_ID
last_telemetry_at = -10.0
incoming = queue.Queue()
connected = False
mission = None
pending = None


def norm_angle(angle):
    return math.atan2(math.sin(angle), math.cos(angle))


def drive(linear=0.0, angular=0.0):
    wl = (linear - angular * WHEEL_SEPARATION_M / 2) / WHEEL_RADIUS_M
    wr = (linear + angular * WHEEL_SEPARATION_M / 2) / WHEEL_RADIUS_M
    scale = min(1.0, MAX_WHEEL_SPEED / max(abs(wl), abs(wr), 0.001))
    left_motor.setVelocity(wl * scale)
    right_motor.setVelocity(wr * scale)


def update_odometry():
    global pose_x, pose_y, pose_heading, prev_left, prev_right
    left = float(left_sensor.getValue())
    right = float(right_sensor.getValue())
    if prev_left is None or prev_right is None:
        prev_left, prev_right = left, right
        return
    dl = (left - prev_left) * WHEEL_RADIUS_M
    dr = (right - prev_right) * WHEEL_RADIUS_M
    prev_left, prev_right = left, right
    delta = (dr - dl) / WHEEL_SEPARATION_M
    distance = 0.5 * (dr + dl)
    pose_x += distance * math.cos(pose_heading + delta / 2)
    pose_y += distance * math.sin(pose_heading + delta / 2)
    pose_heading = norm_angle(pose_heading + delta)


client = mqtt.Client(
    callback_api_version=mqtt.CallbackAPIVersion.VERSION2,
    client_id="smartfleet-webots-route-{}".format(ROBOT_CODE.lower())
)


def on_connect(cli, userdata, flags, reason_code, properties):
    global connected
    connected = (reason_code == 0)
    if connected:
        cli.subscribe([(COMMAND_TOPIC, 1), (STATUS_ACK_TOPIC, 1), (QR_PROGRESS_ACK_TOPIC, 1)])
        print("[MQTT] Connected. Subscribed to mission and progress ACKs")
        # The BE can establish real robot start QR before any task assignment.
        event = {
            "event": "READY",
            "eventId": str(uuid.uuid4()),
            "missionId": 0,
            "arucoId": last_reached_qr,
            "waypointIndex": 0,
        }
        cli.publish(QR_PROGRESS_TOPIC, json.dumps(event), qos=1, retain=False)
    else:
        print("[MQTT] Connection rejected:", reason_code)


def on_disconnect(cli, userdata, flags, reason_code, properties):
    global connected
    connected = False
    print("[MQTT] Disconnected. Motion will stop.")


def on_message(cli, userdata, msg):
    if msg.retain:
        return
    try:
        incoming.put((msg.topic, json.loads(msg.payload.decode("utf-8"))))
    except (ValueError, UnicodeError) as error:
        print("[MQTT] Ignored invalid JSON:", error)


client.on_connect = on_connect
client.on_disconnect = on_disconnect
client.on_message = on_message
client.reconnect_delay_set(min_delay=1, max_delay=5)
client.connect(BROKER_IP, BROKER_PORT, 60)
client.loop_start()


def send_reliable(topic, body, timeout=18.0):
    global pending
    if not connected:
        raise RuntimeError("MQTT disconnected")
    if pending is not None:
        raise RuntimeError("Already waiting for an application ACK")
    body["eventId"] = str(uuid.uuid4())
    pending = {
        "topic": topic,
        "body": body,
        "event_id": body["eventId"],
        "deadline": time.monotonic() + timeout,
        "retry_at": time.monotonic() + 3.0,
        "accepted": None,
        "reason": "",
    }
    client.publish(topic, json.dumps(body), qos=1, retain=False)


def send_status(event, m, reason=None):
    body = {
        "event": event,
        "taskId": m["taskId"],
        "assignmentId": m["assignmentId"],
        "missionId": m["missionId"],
    }
    if reason is not None:
        body["reason"] = str(reason)[:450]
    send_reliable(STATUS_TOPIC, body)
    print("[STATUS]", event, "mission", m["missionId"])


def send_progress(m, index):
    waypoint = m["waypoints"][index]
    send_reliable(QR_PROGRESS_TOPIC, {
        "event": "WAYPOINT_REACHED",
        "missionId": m["missionId"],
        "arucoId": waypoint["arucoId"],
        "waypointIndex": index,
    })
    print("[QR] Arrived", waypoint["arucoId"], "index", index, "stage", waypoint["stage"])


def validate_mission(command):
    if command.get("command") != "EXECUTE_TASK" or command.get("pattern") != "WAYPOINT_ROUTE":
        raise ValueError("Expected EXECUTE_TASK / WAYPOINT_ROUTE (CIRCLE is disabled)")
    for key in ("taskId", "assignmentId", "missionId", "startArUcoId", "pickupArUcoId",
                "deliveryArUcoId", "pickupWaypointIndex", "metersPerGridCell", "waypoints"):
        if key not in command:
            raise ValueError("Missing mission field: " + key)
    waypoints = command["waypoints"]
    if not isinstance(waypoints, list) or len(waypoints) < 2 or len(waypoints) > 100:
        raise ValueError("Route must contain 2..100 ordered QR waypoints")
    for w in waypoints:
        if not all(k in w for k in ("arucoId", "row", "col", "stage")):
            raise ValueError("Invalid QR waypoint")
    if waypoints[0]["arucoId"] != command["startArUcoId"]:
        raise ValueError("First waypoint must be startArUcoId")
    if waypoints[-1]["arucoId"] != command["deliveryArUcoId"]:
        raise ValueError("Last waypoint must be deliveryArUcoId")
    if command["startArUcoId"] != last_reached_qr:
        raise ValueError("BE start QR{} disagrees with current Webots QR{}; fix map start/report".format(
            command["startArUcoId"], last_reached_qr))
    if not (0 < float(command["metersPerGridCell"]) <= 3.0):
        raise ValueError("Invalid metersPerGridCell")
    return command


def start_mission(command):
    global mission, pending
    if mission is not None:
        print("[MISSION] Already executing; ignoring duplicate command")
        return
    command = validate_mission(command)
    print("\n========== PLANNED MISSION ==========\n")
    print("Task", command["taskId"], "Mission", command["missionId"])
    print("Pickup QR", command["pickupArUcoId"], "Delivery QR", command["deliveryArUcoId"])
    print("Route:", " -> ".join("QR{}".format(x["arucoId"]) for x in command["waypoints"]))
    # Local coordinate origin is the first waypoint. On subsequent missions,
    # this can be any QR that Webots last reached.
    command["originMapRow"] = command["waypoints"][0]["row"]
    command["originMapCol"] = command["waypoints"][0]["col"]
    command["originPoseX"] = pose_x
    command["originPoseY"] = pose_y
    command["baseHeading"] = 0.0  # persistent map axes established at Webots startup
    command["waypointIndex"] = 0
    command["phase"] = "STARTING"
    command["legStartedAt"] = robot.getTime()
    mission = command
    send_status("MISSION_STARTED", mission)


def target_xy(m, waypoint):
    scale = float(m["metersPerGridCell"])
    dx = (float(waypoint["col"]) - m["originMapCol"]) * scale
    dy = (float(waypoint["row"]) - m["originMapRow"]) * scale
    # Base heading is the angle between virtual-map positive columns and world
    # robot start orientation. Configure INITIAL_MAP_HEADING_DEGREES correctly.
    rot = m["baseHeading"]
    return (
        m["originPoseX"] + dx * math.cos(rot) - dy * math.sin(rot),
        m["originPoseY"] + dx * math.sin(rot) + dy * math.cos(rot),
    )


def tick_pending():
    global pending
    if pending is None:
        return None
    if pending["accepted"] is not None:
        value = (bool(pending["accepted"]), pending["reason"])
        pending = None
        return value
    now = time.monotonic()
    if now >= pending["deadline"]:
        pending = None
        return (False, "Application ACK timeout")
    if now >= pending["retry_at"] and connected:
        client.publish(pending["topic"], json.dumps(pending["body"]), qos=1, retain=False)
        pending["retry_at"] = now + 3.0
    return None


def fail_mission(reason):
    global mission, pending
    drive()
    print("[MISSION FAILED]", reason)
    if mission is None:
        return
    # The terminal failure event is sent after all other events are cancelled.
    m = mission
    mission = None
    pending = None
    if connected:
        try:
            payload = {
                "eventId": str(uuid.uuid4()),
                "event": "MISSION_FAILED",
                "taskId": m["taskId"],
                "assignmentId": m["assignmentId"],
                "missionId": m["missionId"],
                "reason": str(reason)[:450],
            }
            client.publish(STATUS_TOPIC, json.dumps(payload), qos=1, retain=False)
        except Exception as error:
            print("[MISSION FAILED] Could not publish failure:", error)


def handle_incoming():
    global pending
    while True:
        try:
            topic, data = incoming.get_nowait()
        except queue.Empty:
            return
        if topic == COMMAND_TOPIC:
            if data.get("command") != "EXECUTE_TASK":
                continue
            try:
                start_mission(data)
            except (ValueError, RuntimeError) as error:
                print("[COMMAND REJECTED]", error)
                if mission is not None:
                    fail_mission(error)
                elif all(k in data for k in ("taskId", "assignmentId", "missionId")) and connected:
                    # Never leave a rejected dispatched task silently completing.
                    client.publish(STATUS_TOPIC, json.dumps({
                        "eventId": str(uuid.uuid4()),
                        "event": "MISSION_FAILED",
                        "taskId": data["taskId"],
                        "assignmentId": data["assignmentId"],
                        "missionId": data["missionId"],
                        "reason": str(error)[:450],
                    }), qos=1, retain=False)
        elif topic in (STATUS_ACK_TOPIC, QR_PROGRESS_ACK_TOPIC):
            event_id = data.get("eventId")
            if pending is not None and pending["event_id"] == event_id:
                pending["accepted"] = (data.get("accepted") is True)
                pending["reason"] = str(data.get("reason", data.get("result", "")))
            else:
                # Delayed ACK or previous attempt; safely ignored.
                print("[ACK] Stale/unknown", topic, event_id)


def publish_simulated_telemetry():
    global last_telemetry_at
    if not connected or robot.getTime() - last_telemetry_at < 2.0:
        return
    last_telemetry_at = robot.getTime()
    client.publish(TELEMETRY_TOPIC, json.dumps({
        "batteryPercent": SIMULATED_BATTERY_PERCENT,
        "batteryVoltage": SIMULATED_BATTERY_VOLTAGE,
        "positionX": round(pose_x, 4),
        "positionY": round(pose_y, 4),
        "heading": round(math.degrees(pose_heading), 2),
    }), qos=0, retain=False)


def tick_mission():
    global mission, last_reached_qr
    if mission is None:
        drive()
        return
    m = mission
    if not connected:
        fail_mission("MQTT disconnected during mission")
        return
    result = tick_pending()
    if result is not None:
        if not result[0]:
            fail_mission("Backend rejected or did not ACK: " + result[1])
            return
        if m["phase"] == "STARTING":
            m["phase"] = "DRIVING"
            print("[ACK] MISSION_STARTED confirmed by Backend")
        elif m["phase"] == "WAIT_QR_ACK":
            index = m["waypointIndex"]
            last_reached_qr = m["waypoints"][index]["arucoId"]
            print("[ACK] QR{} accepted by Backend".format(last_reached_qr))
            m["waypointIndex"] += 1
            m["legStartedAt"] = robot.getTime()
            m["phase"] = "DRIVING"
        elif m["phase"] == "FINISHING":
            print("[SUCCESS] Mission", m["missionId"], "COMPLETED in Backend")
            mission = None
            drive()
            return
    if pending is not None:
        drive()
        return
    if m["phase"] != "DRIVING":
        drive()
        return
    index = m["waypointIndex"]
    if index >= len(m["waypoints"]):
        drive()
        if last_reached_qr != m["deliveryArUcoId"]:
            fail_mission("Finished path without reaching delivery QR")
            return
        send_status("MISSION_COMPLETED", m)
        m["phase"] = "FINISHING"
        return
    waypoint = m["waypoints"][index]
    goal_x, goal_y = target_xy(m, waypoint)
    delta_x, delta_y = goal_x - pose_x, goal_y - pose_y
    distance = math.hypot(delta_x, delta_y)
    if distance <= WAYPOINT_TOLERANCE_M:
        drive()
        send_progress(m, index)
        m["phase"] = "WAIT_QR_ACK"
        return
    if robot.getTime() - m["legStartedAt"] > MAX_SIM_SECONDS_PER_WAYPOINT:
        fail_mission("Waypoint QR{} not reached before timeout".format(waypoint["arucoId"]))
        return
    bearing = math.atan2(delta_y, delta_x)
    heading_error = norm_angle(bearing - pose_heading)
    angular = max(-MAX_ANGULAR_RADPS, min(MAX_ANGULAR_RADPS, heading_error * 2.2))
    # Rotate in place before driving; slow near target to minimize overshoot.
    linear = 0.0 if abs(heading_error) > 0.45 else min(CRUISE_SPEED_MPS, distance * 0.65)
    drive(linear, angular)


print("[BOOT] SmartFleet Webots QR route driver", ROBOT_CODE)
print("[CALIBRATION] START_ARUCO_ID=", START_ARUCO_ID,
      "INITIAL_MAP_HEADING_DEGREES=", INITIAL_MAP_HEADING_DEGREES)
print("[CALIBRATION] No camera ArUco recognition: wheel encoder odometry is used.")
try:
    while robot.step(timestep) != -1:
        update_odometry()
        publish_simulated_telemetry()
        handle_incoming()
        tick_mission()
finally:
    drive()
    client.loop_stop()
    client.disconnect()
