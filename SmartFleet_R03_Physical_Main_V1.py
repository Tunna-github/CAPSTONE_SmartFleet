#!/usr/bin/env python3
# SmartFleet R03 PHYSICAL PILOT V1 (Jetson Python 3.6 / ROS Melodic)
# User's original JetRacer cmd_vel + MJPEG reader + ArUco / line vision are reused.
# New: strict WAYPOINT_ROUTE, READY/ACK, waypoint progress, pickup/delivery verification,
# safe bounded motion, application ACK, latched stop, no post-destination turn.
# This file requires the companion opt-in backend patch for ARUCO_ONLY readiness.
# IMPORTANT: Camera-only pilot verifies CHECKPOINT ID, not autonomous heading.
# Its only permitted movement is one manually aligned, supervised QR0 -> QR5 lane.

import os
import sys
import platform
import time
import json
import uuid
import signal
import subprocess
import threading
from urllib.request import Request, urlopen

if platform.machine().lower() in ("aarch64", "arm64"):
    os.environ["OPENBLAS_CORETYPE"] = "ARMV8"
ARUCO_DEPS_DIR = os.path.realpath(os.path.expanduser("~/jetracer_aruco_py36"))
if os.path.isdir(ARUCO_DEPS_DIR) and ARUCO_DEPS_DIR not in sys.path:
    sys.path.insert(0, ARUCO_DEPS_DIR)
import numpy as np
import cv2
import paho.mqtt.client as mqtt

if not hasattr(cv2, "aruco"):
    raise RuntimeError("cv2.aruco unavailable; select the previously tested Python 3 environment")

# ---- Identity & communication ----
ROBOT_CODE = "R03"
MQTT_HOST = "192.168.137.1"
MQTT_PORT = 1884
MQTT_CLIENT_ID = "smartfleet-r03"  # distinct from JetRacer-Battery-R03
TOPIC = "smartfleet/robot/{}/".format(ROBOT_CODE)
COMMAND_TOPIC = TOPIC + "command"
STATUS_TOPIC = TOPIC + "status"
STATUS_ACK_TOPIC = TOPIC + "status-ack"
PROGRESS_TOPIC = TOPIC + "qr-progress"
PROGRESS_ACK_TOPIC = TOPIC + "qr-progress-ack"
LOCALIZATION_MAP_TOPIC = TOPIC + "localization-map"
NAV_CONTROL_TOPIC = TOPIC + "nav-control"

# ---- Physical-pilot safety: defaults refuse movement ----
# Change these ONLY after a supervised wheels-off-ground dry run.
MOTORS_ENABLED = False
OPERATOR_ALIGNED = False  # True ONLY when JetRacer faces QR5 on the short marked track.
START_ARUCO_ID = 0
STOP_ARUCO_ID = 5
MAX_MOVE_SECONDS = 12.0
MAX_MISSION_SECONDS = 120.0
LINE_LINEAR_SPEED = 0.08
LINE_KP = 0.75
LINE_KD = 0.10
LINE_MAX_ANGULAR = 0.35
LINE_STEERING_SIGN = -1.0
LINE_COLOR = "BLACK"  # Change to WHITE only if your actual tape is white.
LINE_BLACK_THRESHOLD = 85
LINE_WHITE_THRESHOLD = 170
LINE_MIN_CONTOUR_AREA = 350.0
LINE_ROI_TOP_RATIO = 0.55
LINE_ROI_SIDE_MARGIN_RATIO = 0.05
LINE_LOST_PRINT_INTERVAL = 1.0
LINE_LOST_ABORT_SECONDS = 1.5

# ---- Camera and ROS: same proven services as original notebook ----
STREAM_URL = "http://127.0.0.1:8080/stream?topic=/csi_cam_0/image_raw&type=mjpeg"
ARUCO_DICT_NAME = "DICT_4X4_50"
VISION_FPS = 6.0
MAX_WIDTH = 640
HTTP_TIMEOUT = 5.0
STALE_AFTER = 1.0
MAX_STREAM_FAILURES = 5
MIN_STABLE_MARKER_FRAMES = 5
READY_INTERVAL_SECONDS = 8.0
PROGRESS_ACK_TIMEOUT = 5.0
ROS_SETUP = """
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
"""

shutdown_event = threading.Event()
abort_event = threading.Event()
mqtt_connected = threading.Event()
movement_lock = threading.Lock()
velocity_bridge_lock = threading.Lock()
velocity_bridge = None
ack_lock = threading.Lock()
pending_acks = {}
client = None
camera_reader = None

# Older controller deliberately removed: no CIRCLE, no extra turn, no 5s drive.

def stop_robot():
    """One-shot safety stop through the existing ROS environment."""
    print("STOP ROBOT", flush=True)
    command = ROS_SETUP + """
rostopic pub -1 /cmd_vel geometry_msgs/Twist \
'{linear: {x: 0.0, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}'
"""
    result = subprocess.run(["bash", "-c", command], timeout=10)
    print("Stop return code:", result.returncode, flush=True)
    if result.returncode != 0:
        raise RuntimeError(
            "ROS stop command failed: {}".format(result.returncode)
        )


class CmdVelBridge:
    """Persistent Python-2 ROS publisher controlled from this Python-3 notebook.

    ROS Melodic on this Jetson uses Python 2 for rospy.  The helper publishes
    /cmd_vel at 10 Hz and has a 0.6-second watchdog: if notebook updates stop,
    the helper automatically publishes zero velocity.
    """
    HELPER_CODE = r"""
from __future__ import print_function
import select
import sys
import time
import rospy
from geometry_msgs.msg import Twist

rospy.init_node('smartfleet_notebook_cmd_vel', anonymous=True, disable_signals=True)
pub = rospy.Publisher('/cmd_vel', Twist, queue_size=1)
rate = rospy.Rate(10)
linear = 0.0
angular = 0.0
last_update = 0.0

def publish(v, w):
    msg = Twist()
    msg.linear.x = float(v)
    msg.angular.z = float(w)
    pub.publish(msg)

try:
    while not rospy.is_shutdown():
        ready, _, _ = select.select([sys.stdin], [], [], 0)
        if ready:
            line = sys.stdin.readline()
            if not line:
                break
            parts = line.strip().split()
            if len(parts) == 2:
                try:
                    linear = float(parts[0])
                    angular = float(parts[1])
                    last_update = time.time()
                except Exception:
                    linear = 0.0
                    angular = 0.0
        if time.time() - last_update > 0.6:
            linear = 0.0
            angular = 0.0
        publish(linear, angular)
        rate.sleep()
finally:
    for _ in range(3):
        try:
            publish(0.0, 0.0)
            time.sleep(0.05)
        except Exception:
            pass
"""

    def __init__(self):
        self.proc = None

    def start(self):
        if self.proc is not None and self.proc.poll() is None:
            return self

        helper_path = "/tmp/smartfleet_cmd_vel_bridge.py"
        with open(helper_path, "w") as helper_file:
            helper_file.write(self.HELPER_CODE)

        command = ROS_SETUP + "\nexec python2 -u {}".format(helper_path)
        self.proc = subprocess.Popen(
            ["bash", "-c", command],
            stdin=subprocess.PIPE,
            start_new_session=True,
        )
        time.sleep(0.5)
        if self.proc.poll() is not None:
            raise RuntimeError(
                "cmd_vel helper exited early with code {}".format(
                    self.proc.returncode
                )
            )
        return self

    def command(self, linear, angular):
        if self.proc is None or self.proc.poll() is not None:
            raise RuntimeError("cmd_vel helper is not running")
        line = "{:.6f} {:.6f}\n".format(float(linear), float(angular))
        try:
            self.proc.stdin.write(line.encode("ascii"))
            self.proc.stdin.flush()
        except Exception as exc:
            raise RuntimeError("Could not update cmd_vel helper: {}".format(exc))

    def stop_motion(self):
        if self.proc is not None and self.proc.poll() is None:
            try:
                self.command(0.0, 0.0)
            except Exception:
                pass

    def close(self):
        proc = self.proc
        self.proc = None
        if proc is None:
            return
        try:
            if proc.poll() is None:
                try:
                    proc.stdin.close()
                except Exception:
                    pass
                os.killpg(proc.pid, signal.SIGTERM)
                proc.wait(timeout=3)
        except Exception:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except Exception:
                pass


def ensure_velocity_bridge():
    global velocity_bridge
    with velocity_bridge_lock:
        if velocity_bridge is None or velocity_bridge.proc is None or velocity_bridge.proc.poll() is not None:
            velocity_bridge = CmdVelBridge().start()
            globals()["_smartfleet_velocity_bridge"] = velocity_bridge
        return velocity_bridge




class _StreamStopped(Exception):
    pass


def _mjpeg_parts(response, stop_event, max_frame_bytes):
    if response.headers.get_content_type() != "multipart/x-mixed-replace":
        raise ValueError("URL did not return multipart MJPEG")

    boundary = response.headers.get_param("boundary")
    if not boundary or len(boundary) > 200:
        raise ValueError("Invalid MJPEG boundary")

    delimiter = b"--" + boundary.encode("ascii")
    max_line = 4096
    max_headers = 16384

    def read_line():
        if stop_event.is_set():
            raise _StreamStopped()
        line = response.readline(max_line + 1)
        if not line:
            raise EOFError("MJPEG server closed the stream")
        if len(line) > max_line:
            raise ValueError("MJPEG header line too long")
        return line

    while not stop_event.is_set():
        used = 0
        line = read_line()
        while line in (b"\r\n", b"\n"):
            used += len(line)
            if used > max_headers:
                raise ValueError("Too many blank MJPEG lines")
            line = read_line()

        marker = line.rstrip(b"\r\n")
        if marker == delimiter + b"--":
            return
        if marker != delimiter:
            raise ValueError("Invalid MJPEG boundary")

        headers = {}
        used = 0
        while True:
            line = read_line()
            used += len(line)
            if used > max_headers:
                raise ValueError("MJPEG headers too large")
            if line in (b"\r\n", b"\n"):
                break
            key, separator, value = line.partition(b":")
            if not separator:
                raise ValueError("Invalid MJPEG header")
            headers[key.strip().lower()] = value.strip()

        if headers.get(b"content-type", b"").split(b";")[0].lower() != b"image/jpeg":
            raise ValueError("MJPEG part is not JPEG")

        try:
            length = int(headers.get(b"content-length", b""))
        except ValueError:
            raise ValueError("Missing valid JPEG Content-Length")

        if length <= 0 or length > max_frame_bytes:
            raise ValueError("Invalid JPEG size")

        payload = bytearray()
        while len(payload) < length:
            if stop_event.is_set():
                raise _StreamStopped()
            chunk = response.read1(min(65536, length - len(payload)))
            if not chunk:
                raise EOFError("Stream ended during JPEG")
            payload.extend(chunk)

        jpeg = bytes(payload)
        if not jpeg.startswith(b"\xff\xd8") or not jpeg.endswith(b"\xff\xd9"):
            raise ValueError("Incomplete JPEG frame")

        yield jpeg


class LatestMjpegReader:
    """Keep one MJPEG connection and only retain the newest JPEG."""
    def __init__(self, url, timeout=5.0, max_failures=5,
                 max_frame_bytes=16 * 1024 * 1024):
        self.url = url
        self.timeout = timeout
        self.max_failures = max_failures
        self.max_frame_bytes = max_frame_bytes
        self.stop_event = threading.Event()
        self.lock = threading.Lock()
        self.thread = None
        self.state = {
            "mode": "starting",
            "message": "Starting...",
            "jpeg": None,
            "sequence": 0,
            "received_at": 0.0,
            "connections": 0,
            "failures": 0,
        }

    def _update(self, **values):
        with self.lock:
            self.state.update(values)

    def latest_state(self):
        with self.lock:
            return dict(self.state)

    def start(self):
        self.thread = threading.Thread(
            target=self._run,
            name="smartfleet-mjpeg-reader",
            daemon=True,
        )
        self.thread.start()
        return self

    def stop(self):
        self.stop_event.set()
        if self.thread is not None:
            self.thread.join(timeout=self.timeout + 1.0)
        return self.thread is None or not self.thread.is_alive()

    def _run(self):
        failures = 0
        try:
            while not self.stop_event.is_set():
                self._update(
                    mode="connecting", jpeg=None, received_at=0.0,
                    message="Connecting to camera stream..."
                )
                try:
                    request = Request(self.url, headers={
                        "Accept": "multipart/x-mixed-replace",
                        "Cache-Control": "no-cache",
                    })

                    with urlopen(request, timeout=self.timeout) as response:
                        with self.lock:
                            self.state["connections"] += 1
                        self._update(
                            mode="waiting",
                            message="Connected; waiting for first JPEG."
                        )

                        for jpeg in _mjpeg_parts(
                                response,
                                self.stop_event,
                                self.max_frame_bytes):
                            if self.stop_event.is_set():
                                break

                            failures = 0
                            with self.lock:
                                self.state.update(
                                    mode="streaming",
                                    message="Receiving camera stream.",
                                    jpeg=jpeg,
                                    received_at=time.monotonic(),
                                    sequence=self.state["sequence"] + 1,
                                    failures=0,
                                )

                        if not self.stop_event.is_set():
                            raise EOFError("MJPEG server ended the stream")

                except _StreamStopped:
                    break
                except Exception as exc:
                    if self.stop_event.is_set():
                        break

                    failures += 1
                    self._update(
                        mode="retrying",
                        jpeg=None,
                        received_at=0.0,
                        message="{}: {}".format(type(exc).__name__, exc),
                        failures=failures,
                    )

                    if failures >= self.max_failures:
                        self._update(mode="failed")
                        break

                    if self.stop_event.wait(1.0):
                        break
        finally:
            if self.stop_event.is_set():
                self._update(
                    mode="stopped",
                    jpeg=None,
                    received_at=0.0,
                    message="Reader stopped.",
                )



aruco = cv2.aruco
if not hasattr(aruco, ARUCO_DICT_NAME):
    raise ValueError("ArUco dictionary does not exist: " + ARUCO_DICT_NAME)

dictionary_id = getattr(aruco, ARUCO_DICT_NAME)
dictionary = (
    aruco.getPredefinedDictionary(dictionary_id)
    if hasattr(aruco, "getPredefinedDictionary")
    else aruco.Dictionary_get(dictionary_id)
)
parameters = (
    aruco.DetectorParameters_create()
    if hasattr(aruco, "DetectorParameters_create")
    else aruco.DetectorParameters()
)
detector = (
    aruco.ArucoDetector(dictionary, parameters)
    if hasattr(aruco, "ArucoDetector")
    else None
)


def decode_camera_frame(jpeg):
    frame = cv2.imdecode(
        np.frombuffer(jpeg, dtype=np.uint8),
        cv2.IMREAD_COLOR,
    )
    if frame is None:
        raise RuntimeError("OpenCV could not decode camera JPEG")

    if MAX_WIDTH and frame.shape[1] > MAX_WIDTH:
        scale = float(MAX_WIDTH) / float(frame.shape[1])
        new_height = max(1, int(round(frame.shape[0] * scale)))
        frame = cv2.resize(frame, (MAX_WIDTH, new_height))
    return frame


def detect_aruco_ids_from_frame(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

    if detector is not None:
        corners, ids, rejected = detector.detectMarkers(gray)
    else:
        corners, ids, rejected = aruco.detectMarkers(
            gray,
            dictionary,
            parameters=parameters,
        )

    if ids is None:
        return []
    return sorted(set(int(marker_id) for marker_id in ids.flatten().tolist()))


def detect_black_line(frame):
    """Return normalized horizontal line error and contour area.

    error = -1 means line is at the far left of the ROI,
    error = +1 means line is at the far right.
    Returns (None, 0) when no sufficiently large black contour is found.
    """
    height, width = frame.shape[:2]
    y0 = int(height * LINE_ROI_TOP_RATIO)
    x_margin = int(width * LINE_ROI_SIDE_MARGIN_RATIO)
    x0 = max(0, x_margin)
    x1 = min(width, width - x_margin)

    roi = frame[y0:height, x0:x1]
    if roi.size == 0:
        return None, 0.0

    gray = cv2.cvtColor(roi, cv2.COLOR_BGR2GRAY)
    _, mask = cv2.threshold(
        gray,
        LINE_BLACK_THRESHOLD if LINE_COLOR == "BLACK" else LINE_WHITE_THRESHOLD,
        255,
        cv2.THRESH_BINARY_INV if LINE_COLOR == "BLACK" else cv2.THRESH_BINARY,
    )

    kernel = np.ones((5, 5), dtype=np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)

    contours_result = cv2.findContours(
        mask,
        cv2.RETR_EXTERNAL,
        cv2.CHAIN_APPROX_SIMPLE,
    )
    contours = contours_result[0] if len(contours_result) == 2 else contours_result[1]

    if not contours:
        return None, 0.0

    contour = max(contours, key=cv2.contourArea)
    area = float(cv2.contourArea(contour))
    if area < LINE_MIN_CONTOUR_AREA:
        return None, area

    moments = cv2.moments(contour)
    if abs(moments.get("m00", 0.0)) < 1e-6:
        return None, area

    cx = float(moments["m10"] / moments["m00"])
    center = (roi.shape[1] - 1) / 2.0
    if center <= 0:
        return None, area

    error = (cx - center) / center
    error = max(-1.0, min(1.0, error))
    return error, area





# ---- Frame acquisition ----
def newest_frame():
    state = camera_reader.latest_state()
    if state["mode"] == "failed":
        raise RuntimeError("Camera reader failed: " + str(state.get("message")))
    if (state.get("jpeg") is None or not state.get("received_at") or
            time.monotonic() - state["received_at"] > STALE_AFTER):
        return None, state.get("sequence", 0)
    return decode_camera_frame(state["jpeg"]), state["sequence"]


def safe_stop():
    with velocity_bridge_lock:
        bridge = velocity_bridge
    if bridge is not None:
        try:
            bridge.stop_motion()
        except Exception as exc:
            print("[STOP] Bridge: {}".format(exc), flush=True)
    # Do not spawn redundant rostopic processes from frequent polling.


def fail_stop(reason):
    abort_event.set()
    safe_stop()
    print("[SAFE HOLD] " + str(reason), flush=True)


def must_be_safe():
    if shutdown_event.is_set() or abort_event.is_set():
        raise RuntimeError("Shutdown or safety hold")
    if not mqtt_connected.is_set():
        raise RuntimeError("Lost MQTT - stopping immediately")


def reliable_event(topic, data, ack_topic, attempts=3, timeout=PROGRESS_ACK_TIMEOUT, allow_held=False):
    """Resend the SAME eventId on lost ACK, never invent duplicate events."""
    if not allow_held:
        must_be_safe()
    elif not mqtt_connected.is_set():
        raise RuntimeError("Cannot ACK a stopped state while MQTT is disconnected")
    event_id = data.setdefault("eventId", str(uuid.uuid4()))
    waiter = {"event": threading.Event(), "reply": None}
    with ack_lock:
        pending_acks[(ack_topic, event_id)] = waiter
    try:
        for attempt in range(attempts):
            if not allow_held:
                must_be_safe()
            elif not mqtt_connected.is_set():
                raise RuntimeError("MQTT disconnected while sending stop/failure ACK")
            result = client.publish(topic, json.dumps(data), qos=1, retain=False)
            if result.rc != mqtt.MQTT_ERR_SUCCESS:
                raise RuntimeError("MQTT publish error {}".format(result.rc))
            if waiter["event"].wait(timeout):
                reply = waiter["reply"]
                if reply is None or reply.get("accepted") is not True:
                    raise RuntimeError("Backend rejected {}: {}".format(data.get("event"), reply))
                return reply
            print("[ACK] Retry {}/{} event {}".format(attempt + 1, attempts, event_id), flush=True)
        raise RuntimeError("Backend application ACK timeout for {}".format(data.get("event")))
    finally:
        with ack_lock:
            pending_acks.pop((ack_topic, event_id), None)


def status_event(kind, mission, reason=None, command_id=None):
    data = {"event": kind, "taskId": mission["taskId"],
            "assignmentId": mission["assignmentId"], "missionId": mission["missionId"]}
    if reason is not None:
        data["reason"] = str(reason)[:300]
    if command_id is not None:
        data["commandId"] = command_id
        data["currentArUcoId"] = robot_marker
    return reliable_event(STATUS_TOPIC, data, STATUS_ACK_TOPIC,
                          allow_held=kind in ("MISSION_FAILED", "CONTROL_STOPPED"))


def progress_event(kind, mission_id, marker_id, waypoint_index, map_revision, stopped=True):
    # headingDeg is deliberately omitted: this limited pilot has no calibrated IMU.
    data = {"event": kind, "missionId": mission_id, "arucoId": marker_id,
            "waypointIndex": waypoint_index, "mapRevision": map_revision,
            "robotStopped": bool(stopped)}
    return reliable_event(PROGRESS_TOPIC, data, PROGRESS_ACK_TOPIC)


class StableScanner:
    def __init__(self):
        self.last_seq = -1
        self.last_id = None
        self.count = 0

    def poll(self, target):
        frame, seq = newest_frame()
        if frame is None or seq == self.last_seq:
            return False, frame, seq
        self.last_seq = seq
        ids = detect_aruco_ids_from_frame(frame)
        found = (target in ids)
        if found:
            self.count += 1
        else:
            self.count = 0
        return self.count >= MIN_STABLE_MARKER_FRAMES, frame, seq


def require_marker(target, timeout=8.0):
    scan = StableScanner()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        must_be_safe()
        ok, _, _ = scan.poll(target)
        if ok:
            return
        time.sleep(0.06)
    raise RuntimeError("Camera cannot verify {} across 5 fresh frames".format(target))


def validate_route(data, current_revision):
    """Reject every command except a verified 2-checkpoint pilot. No fallback."""
    if data.get("command") != "EXECUTE_TASK" or data.get("pattern") != "WAYPOINT_ROUTE":
        raise ValueError("Not a WAYPOINT_ROUTE mission")
    for key in ("taskId", "assignmentId", "missionId"):
        if type(data.get(key)) is not int or data[key] <= 0:
            raise ValueError("Invalid " + key)
    if data.get("verificationRequired") is not True:
        raise ValueError("Backend must require pickup/delivery verification")
    if data.get("mapRevision") != current_revision:
        raise ValueError("Map revision mismatch")
    if (data.get("startArUcoId") != START_ARUCO_ID or
            data.get("pickupArUcoId") != START_ARUCO_ID or
            data.get("deliveryArUcoId") != STOP_ARUCO_ID):
        raise ValueError("Only QR0 pickup -> QR5 delivery is allowed")
    waypoints = data.get("waypoints")
    if not isinstance(waypoints, list) or len(waypoints) != 2:
        raise ValueError("Route must contain exactly QR0, QR5")
    if [x.get("arucoId") for x in waypoints if isinstance(x, dict)] != [START_ARUCO_ID, STOP_ARUCO_ID]:
        raise ValueError("Unexpected waypoint sequence")
    if any(x.get("isIntersection") for x in waypoints):
        raise ValueError("Pilot route cannot traverse an intersection")
    if data.get("pickupWaypointIndex") != 0:
        raise ValueError("Pickup must be first waypoint")
    if data.get("routeToPickup") != [START_ARUCO_ID] or data.get("routeToDelivery") != [START_ARUCO_ID, STOP_ARUCO_ID]:
        raise ValueError("Unexpected leg route")
    return {key: data[key] for key in ("taskId", "assignmentId", "missionId", "mapRevision")}


def wait_for_operator(mission, marker_id, waypoint_index, expected_action, timeout):
    """Keep station presence fresh for backend button while robot is STOPPED."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        must_be_safe()
        safe_stop()
        reply = progress_event("VERIFICATION_STATUS", mission["missionId"], marker_id,
                               waypoint_index, mission["mapRevision"], stopped=True)
        action = reply.get("action")
        if action == expected_action:
            print("[OPERATOR] Backend action {} accepted".format(action), flush=True)
            return
        if action not in ("WAIT_PICKUP_VERIFICATION", "WAIT_DELIVERY_VERIFICATION"):
            raise RuntimeError("Unexpected backend action " + str(action))
        # Keep fresh station presence (backend expires after ~15s).
        for _ in range(12):
            must_be_safe()
            time.sleep(0.2)
    raise RuntimeError("Timed out waiting for {}".format(expected_action))


def follow_line_to_marker(target):
    """One straight lane only. Stop if frame/line/MQTT is lost. Time-bounded."""
    if not MOTORS_ENABLED or not OPERATOR_ALIGNED:
        raise RuntimeError("Motors not armed and physically aligned")
    bridge = ensure_velocity_bridge()
    scanner = StableScanner()
    deadline = time.monotonic() + MAX_MOVE_SECONDS
    last_line = time.monotonic()
    last_pid_at = None
    previous_error = 0.0
    last_notice = 0.0
    destination_seen = False
    try:
        while time.monotonic() < deadline:
            must_be_safe()
            confirmed, frame, seq = scanner.poll(target)
            if frame is None:
                bridge.stop_motion()
                time.sleep(0.04)
                continue
            # Latch the FIRST sighting of destination; never resume driving
            # if marker detection flickers after approaching the stop point.
            if target in detect_aruco_ids_from_frame(frame):
                destination_seen = True
            if destination_seen:
                bridge.stop_motion()
                if confirmed:
                    print("[ARRIVAL] Camera-confirmed QR{}; motors stopped".format(target), flush=True)
                    return
                time.sleep(0.04)
                continue
            error, area = detect_black_line(frame)
            if error is None:
                bridge.stop_motion()
                if time.monotonic() - last_line > LINE_LOST_ABORT_SECONDS:
                    raise RuntimeError("Line lost for too long")
                time.sleep(0.04)
                continue
            now = time.monotonic()
            last_line = now
            derivative = 0.0 if last_pid_at is None else (error - previous_error) / max(.04, now - last_pid_at)
            w = LINE_STEERING_SIGN * (LINE_KP * error + LINE_KD * derivative)
            w = max(-LINE_MAX_ANGULAR, min(LINE_MAX_ANGULAR, w))
            speed = LINE_LINEAR_SPEED * max(.6, 1.0 - .4 * abs(error))
            bridge.command(speed, w)
            if now - last_notice > 1.5:
                print("[LINE] v={:.3f}, steering={:+.3f}, area={:.0f}".format(speed, w, area), flush=True)
                last_notice = now
            previous_error, last_pid_at = error, now
            time.sleep(1.0 / VISION_FPS)
        raise RuntimeError("Physical movement deadline reached without detecting QR{}".format(target))
    finally:
        bridge.stop_motion()


# ---- MQTT controller ----
robot_marker = None
map_revision = None
localization_map = None
last_ready_at = 0.0
ready_ok = False
last_ready_event_id = None
latest_ready_ack_at = 0.0
last_ready_seq = -1
ready_frames = 0
active_mission = None
completed_missions = set()
mission_thread = None


def publish_ready():
    global last_ready_at, last_ready_event_id, ready_ok
    if (not MOTORS_ENABLED or not OPERATOR_ALIGNED or not mqtt_connected.is_set()
            or abort_event.is_set() or map_revision is None):
        return
    if movement_lock.locked():
        return
    frame, seq = newest_frame()
    if frame is None:
        return
    global last_ready_seq, ready_frames, robot_marker
    if seq == last_ready_seq:
        return
    last_ready_seq = seq
    ids = detect_aruco_ids_from_frame(frame)
    if START_ARUCO_ID in ids:
        ready_frames += 1
    else:
        ready_frames = 0
        ready_ok = False
    if ready_frames < MIN_STABLE_MARKER_FRAMES:
        return
    robot_marker = START_ARUCO_ID
    if time.monotonic() - last_ready_at < (READY_INTERVAL_SECONDS if ready_ok else 3.0):
        return
    # Do NOT pretend to have camera+IMU heading. Companion BE patch only permits
    # this opt-in pilot at stationary QR0 with no intersecting roads in the route.
    event_id = str(uuid.uuid4())
    data = {"event": "READY", "eventId": event_id, "missionId": 0,
            "arucoId": START_ARUCO_ID, "waypointIndex": 0,
            "mapRevision": map_revision, "localizationSource": "ARUCO_ONLY",
            "stableFrames": ready_frames, "robotStopped": True}
    pub = client.publish(PROGRESS_TOPIC, json.dumps(data), qos=1, retain=False)
    if pub.rc != mqtt.MQTT_ERR_SUCCESS:
        return
    last_ready_event_id, last_ready_at = event_id, time.monotonic()


def request_localization_map():
    if not mqtt_connected.is_set():
        return
    data = {"event": "LOCALIZATION_CONFIG", "eventId": str(uuid.uuid4()),
            "missionId": 0, "arucoId": START_ARUCO_ID, "waypointIndex": 0}
    client.publish(PROGRESS_TOPIC, json.dumps(data), qos=1, retain=False)


def run_mission(command):
    global active_mission, ready_ok, robot_marker
    try:
        mission = validate_route(command, map_revision)
        active_mission = mission
        if not MOTORS_ENABLED or not OPERATOR_ALIGNED:
            raise RuntimeError("Motors disabled or operator not aligned; refusing mission")
        if not ready_ok:
            raise RuntimeError("Backend did not ACK QR0 READY")
        if not mqtt_connected.is_set():
            raise RuntimeError("MQTT disconnected before start")
        deadline = time.monotonic() + MAX_MISSION_SECONDS
        require_marker(START_ARUCO_ID, timeout=6.0)
        status_event("MISSION_STARTED", mission)
        print("[MISSION] Started mission {}".format(mission["missionId"]), flush=True)
        progress_event("WAYPOINT_REACHED", mission["missionId"], START_ARUCO_ID, 0, map_revision)
        robot_marker = START_ARUCO_ID
        wait_for_operator(mission, START_ARUCO_ID, 0, "CONTINUE", max(1.0, deadline - time.monotonic()))
        must_be_safe()
        follow_line_to_marker(STOP_ARUCO_ID)
        # Re-confirm while at rest; never falsely count a pass-by as arrival.
        require_marker(STOP_ARUCO_ID, timeout=4.0)
        robot_marker = STOP_ARUCO_ID
        progress_event("WAYPOINT_REACHED", mission["missionId"], STOP_ARUCO_ID, 1, map_revision)
        wait_for_operator(mission, STOP_ARUCO_ID, 1, "COMPLETE", max(1.0, deadline - time.monotonic()))
        must_be_safe()
        status_event("MISSION_COMPLETED", mission)
        completed_missions.add(mission["missionId"])
        print("[MISSION] Backend ACKed COMPLETED: {}".format(mission["missionId"]), flush=True)
    except Exception as exc:
        fail_stop(str(exc))
        if active_mission is not None and mqtt_connected.is_set():
            try:
                # A safety hold could reject this event; never clear control locks.
                status_event("MISSION_FAILED", active_mission, str(exc))
            except Exception as report_error:
                print("[MISSION] Could not confirm FAIL event: {}".format(report_error), flush=True)
    finally:
        safe_stop()
        active_mission = None
        ready_ok = False
        movement_lock.release()


def on_connect(_client, userdata, flags, rc):
    global ready_ok
    if rc:
        print("[MQTT] Connection failure: {}".format(rc), flush=True)
        return
    # Reconnect cannot release a previous safety hold.
    ready_ok = False
    mqtt_connected.set()
    _client.subscribe([(COMMAND_TOPIC, 1), (STATUS_ACK_TOPIC, 1),
                       (PROGRESS_ACK_TOPIC, 1), (LOCALIZATION_MAP_TOPIC, 1),
                       (NAV_CONTROL_TOPIC, 1)])
    request_localization_map()
    print("[MQTT] Connected; subscribed R03 mission topics", flush=True)


def on_disconnect(_client, userdata, rc):
    mqtt_connected.clear()
    fail_stop("MQTT disconnect: {}".format(rc))


def on_message(_client, userdata, msg):
    global map_revision, localization_map, ready_ok, robot_marker
    try:
        data = json.loads(msg.payload.decode("utf-8"))
        if not isinstance(data, dict):
            return
        if msg.topic in (STATUS_ACK_TOPIC, PROGRESS_ACK_TOPIC):
            event_id = data.get("eventId")
            with ack_lock:
                w = pending_acks.get((msg.topic, event_id))
                if w is not None:
                    w["reply"] = data
                    w["event"].set()
            if msg.topic == PROGRESS_ACK_TOPIC and event_id == last_ready_event_id:
                ready_ok = data.get("accepted") is True
                if not ready_ok:
                    print("[READY REJECT] {}".format(data.get("reason")), flush=True)
                else:
                    print("[READY] R03 QR0 accepted by backend", flush=True)
            return
        if msg.topic == LOCALIZATION_MAP_TOPIC:
            info = data.get("map")
            if not isinstance(info, dict) or not isinstance(info.get("mapRevision"), int):
                return
            ids = {x.get("arucoId") for x in info.get("markers", []) if isinstance(x, dict)}
            if START_ARUCO_ID not in ids or STOP_ARUCO_ID not in ids:
                fail_stop("Localization map missing QR0 or QR5")
                return
            if map_revision is not None and info["mapRevision"] != map_revision:
                fail_stop("Saved map changed; restart physical pilot")
                return
            map_revision, localization_map = info["mapRevision"], info
            print("[MAP] Backend revision {} accepted, markers={}".format(map_revision, sorted(ids)), flush=True)
            return
        if msg.topic == NAV_CONTROL_TOPIC:
            if data.get("mode") != "GO":
                # No intersection or LiDAR pilot grants can be assumed.
                fail_stop("NAV_CONTROL is not GO; robot must stay stopped")
            return
        if msg.topic != COMMAND_TOPIC or msg.retain:
            return
        kind = data.get("command")
        if kind in ("PAUSE_TASK", "CANCEL_STOP", "FORCE_CANCEL", "RETURN_HOME"):
            fail_stop("Backend safety control " + str(kind))
            if active_mission is not None and data.get("commandId"):
                # RETURN_HOME is never claimed complete by this limited pilot.
                if kind != "RETURN_HOME":
                    threading.Thread(target=control_ack,
                        args=(dict(active_mission), data.get("commandId")), daemon=True).start()
            return
        if kind != "EXECUTE_TASK":
            print("[COMMAND] Ignore unsupported {}".format(kind), flush=True)
            return
        if movement_lock.locked():
            print("[COMMAND] Busy; ignored", flush=True)
            return
        if not ready_ok or not MOTORS_ENABLED or abort_event.is_set():
            print("[COMMAND] R03 NOT ARMED: refusing execution, no motor movement", flush=True)
            return
        try:
            validate_route(data, map_revision)
        except Exception as exc:
            print("[COMMAND] Route rejected: {}".format(exc), flush=True)
            return
        if data.get("missionId") in completed_missions:
            print("[COMMAND] Stale completed mission", flush=True)
            return
        if movement_lock.acquire(False):
            threading.Thread(target=run_mission, args=(data,), daemon=True).start()
    except Exception as exc:
        fail_stop("MQTT handler error: {}".format(exc))


def control_ack(mission, command_id):
    try:
        safe_stop()
        status_event("CONTROL_STOPPED", mission, command_id=command_id)
    except Exception as exc:
        print("[CONTROL] Could not ACK stop: {}".format(exc), flush=True)


def run_controller():
    global client, camera_reader
    print("[BOOT] R03 physical pilot. Motors enabled: {} | Operator aligned: {}".format(
        MOTORS_ENABLED, OPERATOR_ALIGNED), flush=True)
    print("[BOOT] Topic {} | Broker {}:{} | QR0 -> QR5".format(TOPIC, MQTT_HOST, MQTT_PORT), flush=True)
    print("[BOOT] Run battery INA219 Jupyter cell separately for real heartbeat/SoC", flush=True)
    print("[BOOT] Safety: no LiDAR/obstacle sensing in this pilot; human stop required", flush=True)
    camera_reader = LatestMjpegReader(STREAM_URL, timeout=HTTP_TIMEOUT,
                    max_failures=MAX_STREAM_FAILURES).start()
    client = mqtt.Client(client_id=MQTT_CLIENT_ID, clean_session=True)
    client.on_connect = on_connect
    client.on_disconnect = on_disconnect
    client.on_message = on_message
    try:
        client.connect(MQTT_HOST, MQTT_PORT, 30)
        client.loop_start()
        while not shutdown_event.is_set():
            if abort_event.is_set():
                safe_stop()
            elif not movement_lock.locked():
                publish_ready()
            shutdown_event.wait(.2)
    except KeyboardInterrupt:
        print("[STOP] KeyboardInterrupt", flush=True)
    finally:
        shutdown_event.set()
        abort_event.set()
        safe_stop()
        if velocity_bridge is not None:
            velocity_bridge.close()
        try:
            stop_robot()
        except Exception as exc:
            print("[STOP] ROS one-shot stop failed: {}".format(exc), flush=True)
        if camera_reader is not None:
            camera_reader.stop()
        if client is not None:
            client.disconnect()
            client.loop_stop()
        print("[STOP] R03 controller stopped", flush=True)


if __name__ == "__main__":
    run_controller()
