import os
import sys
import platform
import json
import uuid
import signal
import subprocess
import threading
import time
from urllib.request import Request, urlopen

# ============================================================
# PYTHON / OPENCV SETUP
# ============================================================
if platform.machine().lower() in ("aarch64", "arm64"):
    os.environ["OPENBLAS_CORETYPE"] = "ARMV8"

ARUCO_DEPS_DIR = os.path.realpath(os.path.expanduser("~/jetracer_aruco_py36"))
if os.path.isdir(ARUCO_DEPS_DIR) and ARUCO_DEPS_DIR not in sys.path:
    sys.path.insert(0, ARUCO_DEPS_DIR)

import numpy as np
import cv2
import paho.mqtt.client as mqtt

print("Python:", sys.version.split()[0], "|", sys.executable, flush=True)
print("OpenCV:", cv2.__version__, "|", cv2.__file__, flush=True)
print("ArUco available:", hasattr(cv2, "aruco"), flush=True)

if not hasattr(cv2, "aruco"):
    raise RuntimeError(
        "OpenCV currently loaded has no cv2.aruco. "
        "Restart the kernel and make sure ~/jetracer_aruco_py36 is installed."
    )

# ============================================================
# SMARTFLEET CONFIG
# ============================================================
BROKER_IP = "192.168.137.1"
BROKER_PORT = 1883
ROBOT_CODE = "R01"
MQTT_CLIENT_ID = "smartfleet-{}".format(ROBOT_CODE.lower())

COMMAND_TOPIC = "smartfleet/robot/{}/command".format(ROBOT_CODE)
STATUS_TOPIC = "smartfleet/robot/{}/status".format(ROBOT_CODE)
STATUS_ACK_TOPIC = "smartfleet/robot/{}/status-ack".format(ROBOT_CODE)

# ============================================================
# CAMERA / ARUCO CONFIG
# ============================================================
STREAM_URL = "http://127.0.0.1:8080/stream?topic=/csi_cam_0/image_raw&type=mjpeg"
ARUCO_DICT_NAME = "DICT_4X4_50"
START_ARUCO_ID = 0
STOP_ARUCO_ID = 1
VISION_FPS = 6.0
MAX_WIDTH = 640
HTTP_TIMEOUT = 5.0
STALE_AFTER = 3.0
MAX_STREAM_FAILURES = 5

# None = wait indefinitely. Set a number of seconds later if desired.
WAIT_FOR_START_TIMEOUT_SECONDS = None
LINE_FOLLOW_TIMEOUT_SECONDS = None

# ============================================================
# LINE FOLLOW CONFIG
# ============================================================
# Conservative first-test values. Tune on your actual floor/line.
LINE_LINEAR_SPEED = 0.18
LINE_KP = 0.75
LINE_KD = 0.10
LINE_MAX_ANGULAR = 0.70

# Positive image error means the line is to the RIGHT of image center.
# ROS positive angular.z normally turns LEFT, so -1 is the usual sign.
# If the robot steers AWAY from the line, change this to +1.0.
LINE_STEERING_SIGN = -1.0

# Pixels darker than this grayscale value are considered black.
LINE_BLACK_THRESHOLD = 85
LINE_MIN_CONTOUR_AREA = 350.0

# Only inspect the lower part of the camera frame for the path line.
LINE_ROI_TOP_RATIO = 0.55
LINE_ROI_SIDE_MARGIN_RATIO = 0.05

# When the line disappears, command zero immediately.
LINE_LOST_PRINT_INTERVAL = 1.0

# ============================================================
# AFTER ARUCO ID 1: RIGHT TURN, THEN 5 SECONDS STRAIGHT
# ============================================================
# Initial test settings; adjust TURN_SECONDS after observing the real turn.
# Negative angular.z means clockwise/right in conventional ROS cmd_vel.
TURN_LINEAR_SPEED = 0.10
TURN_ANGULAR_SPEED = -0.60
TURN_SECONDS = 1.50
STRAIGHT_LINEAR_SPEED = 0.15
STRAIGHT_SECONDS = 5.0
MOTION_UPDATE_SECONDS = 0.10  # Must be shorter than the bridge's 0.6s watchdog.
PAUSE_BEFORE_TURN_SECONDS = 0.30
PAUSE_AFTER_TURN_SECONDS = 0.30

# Backend compatibility: current BE may still send pattern="CIRCLE".
# It is accepted as a legacy value, but this notebook performs line-following.
SUPPORTED_PATTERNS = ("CIRCLE", "LINE_FOLLOW", "QR_LINE_FOLLOW")

# ============================================================
# ROS MOVEMENT CONFIG
# ============================================================
ROS_SETUP = """
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
"""

# ============================================================
# SHARED STATE
# ============================================================
movement_lock = threading.Lock()
ack_lock = threading.Lock()
pending_acks = {}
mqtt_connected = threading.Event()
shutdown_event = threading.Event()

velocity_bridge_lock = threading.Lock()
velocity_bridge = None

# ============================================================
# CLEAN UP PREVIOUS RUN IN SAME JUPYTER KERNEL
# ============================================================
_previous_reader = globals().get("_smartfleet_camera_reader")
if _previous_reader is not None:
    try:
        _previous_reader.stop()
    except Exception:
        pass

_previous_client = globals().get("_smartfleet_mqtt_client")
if _previous_client is not None:
    try:
        _previous_client.loop_stop()
    except Exception:
        pass
    try:
        _previous_client.disconnect()
    except Exception:
        pass

_previous_bridge = globals().get("_smartfleet_velocity_bridge")
if _previous_bridge is not None:
    try:
        _previous_bridge.close()
    except Exception:
        pass

# ============================================================
# MQTT ACK LOGIC
# ============================================================
def wait_for_ack(event_id, timeout=10):
    with ack_lock:
        waiter = pending_acks.get(event_id)

    if waiter is None:
        raise RuntimeError("ACK waiter not found for eventId {}".format(event_id))

    print("Waiting for BE ACK:", event_id, flush=True)
    received = waiter["event"].wait(timeout)

    if not received:
        with ack_lock:
            pending_acks.pop(event_id, None)
        raise RuntimeError(
            "Timeout waiting for BE ACK for eventId {}".format(event_id)
        )

    with ack_lock:
        ack = pending_acks.pop(event_id, None)

    if ack is None:
        raise RuntimeError("ACK disappeared for eventId {}".format(event_id))

    accepted = ack.get("accepted")
    result = ack.get("result")
    print("ACK result:", result, flush=True)

    if accepted is not True:
        raise RuntimeError(
            "Backend rejected event {}. Result={}".format(event_id, result)
        )

    return ack


def send_status(event, task_id, assignment_id, mission_id,
                reason=None, wait_ack=True):
    if not mqtt_connected.wait(timeout=10):
        raise RuntimeError("MQTT is not connected")

    event_id = str(uuid.uuid4())
    payload = {
        "eventId": event_id,
        "event": event,
        "taskId": task_id,
        "assignmentId": assignment_id,
        "missionId": mission_id,
    }
    if reason is not None:
        payload["reason"] = reason

    payload_text = json.dumps(payload)

    if wait_ack:
        with ack_lock:
            pending_acks[event_id] = {"event": threading.Event()}

    print("\n========================================", flush=True)
    print("SEND STATUS", flush=True)
    print("========================================", flush=True)
    print("Event:", event, flush=True)
    print("Event ID:", event_id, flush=True)
    print("Payload:", payload_text, flush=True)

    result = client.publish(
        STATUS_TOPIC, payload_text, qos=1, retain=False
    )

    print("MQTT publish result:", result.rc, flush=True)

    if result.rc != mqtt.MQTT_ERR_SUCCESS:
        if wait_ack:
            with ack_lock:
                pending_acks.pop(event_id, None)
        raise RuntimeError(
            "MQTT publish failed with code {}".format(result.rc)
        )

    result.wait_for_publish(timeout=10)
    if not result.is_published():
        if wait_ack:
            with ack_lock:
                pending_acks.pop(event_id, None)
        raise RuntimeError("MQTT message was not published")

    print("MQTT publish completed", flush=True)

    if wait_ack:
        return wait_for_ack(event_id, timeout=10)
    return None

# ============================================================
# ROBOT MOVEMENT
# ============================================================
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


def timed_motion(bridge, linear, angular, duration, label):
    """Send a fresh cmd_vel during a timed maneuver (bridge has a watchdog)."""
    if duration < 0:
        raise ValueError("Motion duration must be nonnegative")
    print("[MOTION] {} for {:.2f}s | v={:.2f}, w={:+.2f}".format(
        label, duration, linear, angular), flush=True)
    deadline = time.monotonic() + duration
    try:
        while not shutdown_event.is_set():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            bridge.command(linear, angular)
            # Event-based sleep exits promptly when notebook shuts down.
            shutdown_event.wait(min(MOTION_UPDATE_SECONDS, remaining))
        if shutdown_event.is_set():
            raise RuntimeError("Motion interrupted by notebook shutdown")
    finally:
        bridge.stop_motion()


def turn_right_then_drive_straight():
    """Perform post-ID1 maneuver; only return after a final stop."""
    print("\n[ID 1] Starting right turn + straight approach", flush=True)
    bridge = ensure_velocity_bridge()
    try:
        bridge.stop_motion()
        if shutdown_event.wait(PAUSE_BEFORE_TURN_SECONDS):
            raise RuntimeError("Interrupted before right turn")

        timed_motion(bridge, TURN_LINEAR_SPEED, TURN_ANGULAR_SPEED,
                     TURN_SECONDS, "TURN RIGHT")
        if shutdown_event.wait(PAUSE_AFTER_TURN_SECONDS):
            raise RuntimeError("Interrupted after right turn")

        timed_motion(bridge, STRAIGHT_LINEAR_SPEED, 0.0,
                     STRAIGHT_SECONDS, "DRIVE STRAIGHT")
        bridge.stop_motion()
        stop_robot()
        print("[MOTION] Final stop confirmed; ready to report completion", flush=True)
    finally:
        bridge.stop_motion()


def cancel_movement_and_stop():
    shutdown_event.set()
    with velocity_bridge_lock:
        bridge = velocity_bridge
    if bridge is not None:
        try:
            bridge.stop_motion()
        except Exception:
            pass
    try:
        stop_robot()
    except Exception as exc:
        print("Could not stop robot during cleanup:", repr(exc), flush=True)

# ============================================================
# MJPEG STREAM READER
# Camera remains live. ArUco processing is NOT done here.
# ============================================================
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

# ============================================================
# VISION: ARUCO + BLACK LINE
# ============================================================
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
        LINE_BLACK_THRESHOLD,
        255,
        cv2.THRESH_BINARY_INV,
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


def _fresh_camera_frame(last_sequence, last_status_at):
    """Return (frame, sequence, status_time), or (None, last_sequence, status_time)."""
    now = time.monotonic()
    state = camera_reader.latest_state()

    if state["mode"] == "failed":
        raise RuntimeError(
            "Camera stream failed: {}".format(state.get("message"))
        )

    jpeg = state.get("jpeg")
    sequence = state.get("sequence", 0)
    received_at = state.get("received_at", 0.0)

    if (jpeg is None or sequence == last_sequence or
            received_at <= 0.0 or now - received_at > STALE_AFTER):
        if now - last_status_at >= 2.0:
            print(
                "[VISION] Waiting for fresh frame | stream={}".format(
                    state.get("mode")
                ),
                flush=True,
            )
            last_status_at = now
        return None, last_sequence, last_status_at

    return decode_camera_frame(jpeg), sequence, last_status_at


def wait_for_aruco_id(target_id, timeout=None):
    """Scanner is active only during an accepted mission."""
    print("\n========================================", flush=True)
    print("ARUCO SCANNER ACTIVE", flush=True)
    print("========================================", flush=True)
    print("Waiting for ArUco ID:", target_id, flush=True)

    started_at = time.monotonic()
    last_sequence = -1
    last_ids = None
    last_status_at = 0.0
    min_interval = 1.0 / VISION_FPS
    next_process_at = 0.0

    while not shutdown_event.is_set():
        now = time.monotonic()
        if timeout is not None and now - started_at >= timeout:
            raise RuntimeError(
                "Timed out waiting for ArUco ID {}".format(target_id)
            )

        if now < next_process_at:
            time.sleep(min(0.03, next_process_at - now))
            continue

        frame, sequence, last_status_at = _fresh_camera_frame(
            last_sequence,
            last_status_at,
        )
        if frame is None:
            time.sleep(0.03)
            continue

        last_sequence = sequence
        next_process_at = now + min_interval
        ids = detect_aruco_ids_from_frame(frame)

        if ids != last_ids or now - last_status_at >= 1.0:
            print("[SCAN] IDs:", ids if ids else "none", flush=True)
            last_ids = ids
            last_status_at = now

        if target_id in ids:
            print("\n*** ARUCO ID {} FOUND ***".format(target_id), flush=True)
            return target_id

    raise RuntimeError("Scanner stopped because notebook is shutting down")


def follow_black_line_until_aruco(stop_id, timeout=None):
    """Follow a dark line and stop immediately when stop_id is detected."""
    print("\n========================================", flush=True)
    print("BLACK LINE FOLLOW ACTIVE", flush=True)
    print("========================================", flush=True)
    print("Following line until ArUco ID:", stop_id, flush=True)

    bridge = ensure_velocity_bridge()
    started_at = time.monotonic()
    last_sequence = -1
    last_status_at = 0.0
    next_process_at = 0.0
    min_interval = 1.0 / VISION_FPS
    previous_error = 0.0
    previous_time = None
    line_was_visible = False
    last_line_lost_print = 0.0

    try:
        while not shutdown_event.is_set():
            now = time.monotonic()
            if timeout is not None and now - started_at >= timeout:
                raise RuntimeError(
                    "Timed out following line before ArUco ID {}".format(stop_id)
                )

            if now < next_process_at:
                time.sleep(min(0.02, next_process_at - now))
                continue

            frame, sequence, last_status_at = _fresh_camera_frame(
                last_sequence,
                last_status_at,
            )
            if frame is None:
                bridge.stop_motion()
                time.sleep(0.03)
                continue

            last_sequence = sequence
            next_process_at = now + min_interval

            # Stop marker gets priority over motion calculation.
            ids = detect_aruco_ids_from_frame(frame)
            if stop_id in ids:
                bridge.stop_motion()
                print("\n*** STOP ARUCO ID {} FOUND ***".format(stop_id), flush=True)
                print("Robot stopping at destination marker.", flush=True)
                return stop_id

            error, area = detect_black_line(frame)
            if error is None:
                bridge.stop_motion()
                line_was_visible = False
                previous_time = None
                if now - last_line_lost_print >= LINE_LOST_PRINT_INTERVAL:
                    print(
                        "[LINE] Black line NOT found -> STOP | largest area={:.0f}".format(area),
                        flush=True,
                    )
                    last_line_lost_print = now
                continue

            dt = 0.0 if previous_time is None else max(0.001, now - previous_time)
            derivative = 0.0 if previous_time is None else (error - previous_error) / dt

            steering = LINE_STEERING_SIGN * (
                LINE_KP * error + LINE_KD * derivative
            )
            steering = max(-LINE_MAX_ANGULAR, min(LINE_MAX_ANGULAR, steering))

            # Slow slightly on bigger steering corrections.
            speed_scale = max(0.55, 1.0 - 0.45 * abs(error))
            linear = LINE_LINEAR_SPEED * speed_scale

            bridge.command(linear, steering)

            if (not line_was_visible) or now - last_status_at >= 1.0:
                print(
                    "[LINE] following | error={:+.3f} area={:.0f} v={:.3f} w={:+.3f} IDs={}".format(
                        error, area, linear, steering, ids if ids else "none"
                    ),
                    flush=True,
                )
                last_status_at = now

            line_was_visible = True
            previous_error = error
            previous_time = now

    finally:
        bridge.stop_motion()
        try:
            stop_robot()
        except Exception as exc:
            print("Stop warning after line follow:", repr(exc), flush=True)

    raise RuntimeError("Line follower stopped because notebook is shutting down")

# ============================================================
# MISSION EXECUTION
# ============================================================
def execute_task(task_id, assignment_id, mission_id):
    print("\n========================================", flush=True)
    print("EXECUTE TASK", flush=True)
    print("========================================", flush=True)
    print("Task:", task_id, flush=True)
    print("Assignment:", assignment_id, flush=True)
    print("Mission:", mission_id, flush=True)

    try:
        # STEP 1: backend changes mission to EXECUTING first.
        print("\nSending MISSION_STARTED...", flush=True)
        started_ack = send_status(
            "MISSION_STARTED",
            task_id,
            assignment_id,
            mission_id,
        )
        print("MISSION_STARTED accepted:", started_ack, flush=True)
        print("Backend confirmed EXECUTING state.", flush=True)

        # STEP 2: only after an accepted task do we start vision processing.
        found_start_id = wait_for_aruco_id(
            START_ARUCO_ID,
            timeout=WAIT_FOR_START_TIMEOUT_SECONDS,
        )
        print("Start marker confirmed:", found_start_id, flush=True)

        # STEP 3: follow the black line while continuing to scan ArUco.
        found_stop_id = follow_black_line_until_aruco(
            STOP_ARUCO_ID,
            timeout=LINE_FOLLOW_TIMEOUT_SECONDS,
        )
        print("Destination marker confirmed:", found_stop_id, flush=True)

        # STEP 4: after ID 1, turn right and travel straight for 5 seconds.
        # The existing line follower has already stopped in its finally block.
        turn_right_then_drive_straight()

        # STEP 5: only report completion after the final robot stop.
        print("\nSending MISSION_COMPLETED...", flush=True)
        completed_ack = send_status(
            "MISSION_COMPLETED",
            task_id,
            assignment_id,
            mission_id,
        )
        print("MISSION_COMPLETED accepted:", completed_ack, flush=True)

        print("\n========================================", flush=True)
        print("MISSION COMPLETED", flush=True)
        print("========================================", flush=True)

    except Exception as error:
        print("\n========================================", flush=True)
        print("MISSION ERROR", flush=True)
        print("========================================", flush=True)
        print(repr(error), flush=True)

        try:
            with velocity_bridge_lock:
                bridge = velocity_bridge
            if bridge is not None:
                bridge.stop_motion()
            stop_robot()
        except Exception as stop_error:
            print("Could not stop robot:", repr(stop_error), flush=True)

        if not shutdown_event.is_set():
            try:
                send_status(
                    "MISSION_FAILED",
                    task_id,
                    assignment_id,
                    mission_id,
                    reason=str(error),
                )
            except Exception as mqtt_error:
                print(
                    "Could not report MISSION_FAILED:",
                    repr(mqtt_error),
                    flush=True,
                )
    finally:
        if movement_lock.locked():
            movement_lock.release()

        print("\nWorker finished", flush=True)
        print("Vision processing: IDLE", flush=True)
        print("Robot ready for next mission", flush=True)

# ============================================================
# MQTT CALLBACKS
# ============================================================
def on_connect(client, userdata, flags, rc):
    print("\nMQTT CONNECT:", rc, flush=True)
    if rc == 0:
        mqtt_connected.set()
        client.subscribe(COMMAND_TOPIC, qos=1)
        client.subscribe(STATUS_ACK_TOPIC, qos=1)
        print("Subscribed:", COMMAND_TOPIC, flush=True)
        print("Subscribed:", STATUS_ACK_TOPIC, flush=True)
    else:
        mqtt_connected.clear()


def on_disconnect(client, userdata, rc):
    mqtt_connected.clear()
    print("\nMQTT DISCONNECTED:", rc, flush=True)


def handle_status_ack(msg):
    try:
        payload_text = msg.payload.decode("utf-8")
        print("\n========================================", flush=True)
        print("BACKEND ACK", flush=True)
        print("========================================", flush=True)
        print("Payload:", payload_text, flush=True)

        data = json.loads(payload_text)
        event_id = data.get("eventId")

        if not event_id:
            print("Ignored ACK without eventId", flush=True)
            return

        with ack_lock:
            waiter = pending_acks.get(event_id)
            if waiter is None:
                print(
                    "ACK for unknown/already processed eventId:",
                    event_id,
                    flush=True,
                )
                return

            waiter["accepted"] = data.get("accepted")
            waiter["result"] = data.get("result")
            waiter["assignmentId"] = data.get("assignmentId")
            waiter["event"].set()

    except Exception as error:
        print("ACK processing error:", repr(error), flush=True)


def handle_command(msg):
    if msg.retain:
        print("Ignored retained command", flush=True)
        return

    try:
        payload_text = msg.payload.decode("utf-8")

        print("\n========================================", flush=True)
        print("COMMAND RECEIVED", flush=True)
        print("========================================", flush=True)
        print("Topic:", msg.topic, flush=True)
        print("Payload:", payload_text, flush=True)

        data = json.loads(payload_text)
        if not isinstance(data, dict):
            print("Payload must be JSON object", flush=True)
            return

        command = data.get("command")
        task_id = data.get("taskId")
        assignment_id = data.get("assignmentId")
        mission_id = data.get("missionId")
        pattern = data.get("pattern")

        print("Command:", command, flush=True)
        print("Task ID:", task_id, flush=True)
        print("Assignment ID:", assignment_id, flush=True)
        print("Mission ID:", mission_id, flush=True)
        print("Pattern:", pattern, flush=True)

        if command != "EXECUTE_TASK":
            print("Unsupported command", flush=True)
            return

        if not isinstance(task_id, int) or task_id <= 0:
            print("Invalid taskId", flush=True)
            return

        if not isinstance(assignment_id, int) or assignment_id <= 0:
            print("Invalid assignmentId", flush=True)
            return

        if not isinstance(mission_id, int) or mission_id <= 0:
            print("Invalid missionId", flush=True)
            return

        if pattern not in SUPPORTED_PATTERNS:
            print("Unsupported pattern:", pattern, flush=True)
            try:
                send_status(
                    "MISSION_FAILED",
                    task_id,
                    assignment_id,
                    mission_id,
                    reason="Unsupported pattern: {}".format(pattern),
                )
            except Exception as error:
                print("Could not send failure:", repr(error), flush=True)
            return

        if pattern == "CIRCLE":
            print(
                "Legacy pattern CIRCLE accepted; executing QR + black-line mission.",
                flush=True,
            )

        # Only one active mission at a time.
        if not movement_lock.acquire(False):
            print("Robot already running a mission", flush=True)
            return

        print("\nSTART MISSION WORKER", flush=True)
        worker = threading.Thread(
            target=execute_task,
            args=(task_id, assignment_id, mission_id),
            name="smartfleet-mission-{}".format(mission_id),
            daemon=True,
        )

        try:
            worker.start()
        except Exception:
            movement_lock.release()
            raise

    except Exception as error:
        print("MESSAGE ERROR:", repr(error), flush=True)


def on_message(client, userdata, msg):
    if msg.topic == STATUS_ACK_TOPIC:
        handle_status_ack(msg)
        return

    if msg.topic == COMMAND_TOPIC:
        handle_command(msg)
        return

    print("Ignored MQTT topic:", msg.topic, flush=True)

# ============================================================
# START CAMERA READER + MQTT
# ============================================================
camera_reader = LatestMjpegReader(
    STREAM_URL,
    timeout=HTTP_TIMEOUT,
    max_failures=MAX_STREAM_FAILURES,
).start()
_smartfleet_camera_reader = camera_reader

client = mqtt.Client(client_id=MQTT_CLIENT_ID)
client.on_connect = on_connect
client.on_disconnect = on_disconnect
client.on_message = on_message
_smartfleet_mqtt_client = client

print("\n========================================", flush=True)
print("SmartFleet JetRacer + ArUco + black-line navigation", flush=True)
print("========================================", flush=True)
print("Robot:", ROBOT_CODE, flush=True)
print("Broker:", BROKER_IP, flush=True)
print("Command topic:", COMMAND_TOPIC, flush=True)
print("Status topic:", STATUS_TOPIC, flush=True)
print("ACK topic:", STATUS_ACK_TOPIC, flush=True)
print("Start ArUco ID:", START_ARUCO_ID, flush=True)
print("Stop ArUco ID:", STOP_ARUCO_ID, flush=True)
print("Vision state: IDLE until MISSION_STARTED ACK", flush=True)

client.connect(BROKER_IP, BROKER_PORT, 60)
client.loop_start()

print("\nREADY", flush=True)
print("Waiting for EXECUTE_TASK...", flush=True)

# Keep notebook cell alive while MQTT runs in background threads.
try:
    while True:
        if shutdown_event.wait(1.0):
            break
except KeyboardInterrupt:
    print("\nKeyboardInterrupt -> shutting down", flush=True)
finally:
    shutdown_event.set()

    with velocity_bridge_lock:
        bridge = velocity_bridge
    if bridge is not None:
        try:
            bridge.stop_motion()
            bridge.close()
        except Exception as exc:
            print("Velocity bridge stop warning:", repr(exc), flush=True)

    try:
        stop_robot()
    except Exception as exc:
        print("Final stop warning:", repr(exc), flush=True)

    try:
        camera_reader.stop()
    except Exception as exc:
        print("Camera reader stop warning:", repr(exc), flush=True)

    try:
        client.loop_stop()
    except Exception:
        pass

    try:
        client.disconnect()
    except Exception:
        pass

    mqtt_connected.clear()
    print("SmartFleet notebook stopped.", flush=True)
