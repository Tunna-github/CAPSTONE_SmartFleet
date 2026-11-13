import os
import sys
import platform
import subprocess
import signal
import json

# Dat truoc khi nap NumPy/OpenCV. Chi ap dung ARMV8 tren ARM64.
# Workaround cho loi OpenBLAS CPU detection da duoc bao cao tren Jetson.
if platform.machine().lower() in ("aarch64", "arm64"):
    os.environ["OPENBLAS_CORETYPE"] = "ARMV8"

print("Python:", sys.version.split()[0], "|", sys.executable,
      "|", platform.machine(), flush=True)
print("OPENBLAS_CORETYPE:", os.environ.get("OPENBLAS_CORETYPE", "auto"),
      flush=True)

# Ban OpenCV he thong 4.1.1 trong anh loi khong co cv2.aruco.
# Cai contrib vao thu muc rieng bang lenh Terminal trong phan huong dan.
ARUCO_DEPS_DIR = os.path.realpath(os.path.expanduser("~/jetracer_aruco_py36"))
if not os.path.isdir(os.path.join(ARUCO_DEPS_DIR, "cv2")):
    raise RuntimeError(
        "Chua co OpenCV contrib tai {}. "
        "Hay chay lenh cai dat o dau notebook trong Terminal Jetson, "
        "sau do Restart Kernel va chay lai cell."
        .format(ARUCO_DEPS_DIR))

_cached_cv2 = sys.modules.get("cv2")
if _cached_cv2 is not None:
    _cached_cv2_path = os.path.realpath(getattr(_cached_cv2, "__file__", "") or "")
    if (not _cached_cv2_path.startswith(ARUCO_DEPS_DIR + os.sep)
            or not hasattr(_cached_cv2, "aruco")):
        raise RuntimeError(
            "Kernel da nap OpenCV tu {}. "
            "Chon Kernel > Restart Kernel de nap ban contrib, roi chay lai cell."
            .format(_cached_cv2_path))

# Chi doi duong dan import cua kernel nay; process kiem tra se dung cung path.
sys.path[:] = [ARUCO_DEPS_DIR] + [p for p in sys.path if p != ARUCO_DEPS_DIR]
print("OpenCV contrib folder:", ARUCO_DEPS_DIR, flush=True)


_native_import_code = r'''
import sys
import json
sys.path[:] = json.loads(sys.argv[1])
print("[CHECK] Dang nap NumPy trong Python rieng...", flush=True)
import numpy as np
print("[CHECK OK] NumPy:", np.__version__, "|", np.__file__, flush=True)
print("[CHECK] Dang nap OpenCV trong Python rieng...", flush=True)
import cv2
print("[CHECK OK] OpenCV:", cv2.__version__, "|", cv2.__file__, flush=True)
if not hasattr(cv2, "aruco"):
    raise RuntimeError("Ban OpenCV nay khong co cv2.aruco.")
print("[CHECK OK] Co cv2.aruco.", flush=True)
'''


def _check_native_imports():
    """Thu import trong process con de hien traceback/exit code neu no chet."""
    print("[0/4] Kiem tra NumPy/OpenCV; cho toi da 30 giay...", flush=True)
    try:
        result = subprocess.run(
            [sys.executable, "-u", "-X", "faulthandler", "-c",
             _native_import_code, json.dumps(sys.path)],
            env=os.environ.copy(),
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=30.0)
    except subprocess.TimeoutExpired as exc:
        output = exc.output or b""
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        if output:
            print(output, end="" if output.endswith("\n") else "\n", flush=True)
        raise RuntimeError(
            "Import NumPy/OpenCV chua xong sau 30 giay. "
            "Da dung process kiem tra; chua bat dau quet. "
            "Gui lai cac dong CHECK va loi nay.") from None

    output = result.stdout.decode("utf-8", errors="replace")
    if output:
        print(output, end="" if output.endswith("\n") else "\n", flush=True)
    print("Import check return code:", result.returncode, flush=True)
    if result.returncode != 0:
        reason = "exit code {}".format(result.returncode)
        if result.returncode < 0:
            number = -result.returncode
            try:
                name = signal.Signals(number).name
            except ValueError:
                name = "SIGNAL_{}".format(number)
            reason = "{} ({})".format(name, result.returncode)
        raise RuntimeError(
            "Python kiem tra import da dung: {}. "
            "Chua bat dau quet. Gui lai cac dong CHECK va loi phia tren."
            .format(reason))


_check_native_imports()
# Import check thanh cong chi xac nhan process con. Kernel van import ben duoi.


print("[1/4] Dang nap NumPy...", flush=True)
import numpy as np
print("[OK] NumPy:", np.__version__, flush=True)

print("[2/4] Dang nap OpenCV...", flush=True)
import cv2
print("[OK] OpenCV:", cv2.__version__, "|", cv2.__file__, flush=True)

from html import escape
from IPython.display import HTML, display

# --- CAU HINH ---
STREAM_URL = "http://127.0.0.1:8080/stream?topic=/csi_cam_0/image_raw&type=mjpeg"
ARUCO_DICT_NAME = "DICT_4X4_50"  # Phai khop voi bo ma ban da in.
TARGET_FPS = 3.0                # Gioi han xu ly ArUco, reader van doc lien tuc.
MAX_WIDTH = 800                 # 1280 hoac 0 neu marker nho/o xa.
HTTP_TIMEOUT = 5.0
STALE_AFTER = 3.0
MAX_FAILURES = 5
TEXT_UPDATE_SECONDS = 1.0       # ID doi thi hien ngay; neu khong, cap nhat 1s/lan.

# --- HANH DONG KHI NHAN DIEN ID 0 ---
TRIGGER_ARUCO_ID = 0
CIRCLE_SECONDS = 30.0
REARM_ABSENT_SECONDS = 2.0  # Chi cho trigger lai sau khi ID 0 bien mat lien tuc.
ROS_SETUP = """
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
"""

_previous_reader = globals().get("_aruco_mjpeg_reader")
if _previous_reader is not None:
    print("Dang dung reader cu...", flush=True)
    if not _previous_reader.stop():
        raise RuntimeError(
            "Reader cu van dang dong ket noi. Cho vai giay roi chay lai cell.")



import threading
import time
from urllib.request import Request, urlopen


# ============================================================
# ROBOT MOVEMENT: circle for 30s when ArUco ID 0 is detected
# Values are taken from the movement file supplied by the user.
# ============================================================
_movement_lock = threading.Lock()
_movement_active = threading.Event()
_movement_cancel = threading.Event()
_movement_process = None


def stop_robot():
    command = ROS_SETUP + """
rostopic pub -1 /cmd_vel geometry_msgs/Twist \
'{linear: {x: 0.0, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.0}}'
"""
    result = subprocess.run(["bash", "-c", command], timeout=10)
    if result.returncode != 0:
        raise RuntimeError(
            "ROS stop command failed: {}".format(result.returncode))


def _terminate_movement_process():
    global _movement_process
    with _movement_lock:
        proc = _movement_process
    if proc is None or proc.poll() is not None:
        return
    try:
        os.killpg(proc.pid, signal.SIGTERM)
        proc.wait(timeout=3)
    except Exception:
        try:
            os.killpg(proc.pid, signal.SIGKILL)
        except Exception:
            pass


def run_circle():
    """Run the JetRacer in a circle for 30 seconds, then stop it."""
    global _movement_process
    print("[MOVE] ID 0 detected -> RUN CIRCLE for 30 seconds", flush=True)

    # Same motion from the supplied SmartFleet test file:
    # linear.x = 0.6, angular.z = 0.2, published at 10 Hz for 30s.
    command = ROS_SETUP + """
timeout 30s rostopic pub -r 10 /cmd_vel geometry_msgs/Twist \
'{linear: {x: 0.6, y: 0.0, z: 0.0}, angular: {x: 0.0, y: 0.0, z: 0.2}}'
"""

    try:
        proc = subprocess.Popen(
            ["bash", "-c", command],
            start_new_session=True)
        with _movement_lock:
            _movement_process = proc

        # Poll so Kernel Interrupt can cancel movement immediately.
        while proc.poll() is None:
            if _movement_cancel.is_set():
                _terminate_movement_process()
                break
            time.sleep(0.1)

        rc = proc.poll()
        if _movement_cancel.is_set():
            print("[MOVE] Circle cancelled.", flush=True)
        elif rc == 124:
            print("[MOVE] 30 seconds complete.", flush=True)
        elif rc == 0:
            print("[MOVE] Circle publisher ended normally.", flush=True)
        else:
            print("[MOVE] Circle publisher return code: {}".format(rc), flush=True)
    except Exception as exc:
        print("[MOVE ERROR] {}: {}".format(type(exc).__name__, exc), flush=True)
    finally:
        with _movement_lock:
            _movement_process = None
        try:
            stop_robot()
        except Exception as exc:
            print("[STOP ERROR] {}: {}".format(type(exc).__name__, exc), flush=True)
        _movement_active.clear()
        print("[MOVE] Robot stopped; circle action finished.", flush=True)


def start_circle_if_idle():
    """Start one circle action in the background; never start two at once."""
    if _movement_active.is_set():
        return False
    _movement_cancel.clear()
    _movement_active.set()
    worker = threading.Thread(
        target=run_circle, name="aruco-id0-circle", daemon=True)
    worker.start()
    return True


def cancel_circle_and_stop():
    """Used when the notebook is interrupted/stopped."""
    _movement_cancel.set()
    _terminate_movement_process()
    try:
        stop_robot()
    except Exception as exc:
        print("[STOP ERROR] {}: {}".format(type(exc).__name__, exc), flush=True)


class _StreamStopped(Exception):
    pass


def _mjpeg_parts(response, stop_event, max_frame_bytes):
    """Doc tung phan JPEG theo Content-Length cua web_video_server."""
    if response.headers.get_content_type() != "multipart/x-mixed-replace":
        raise ValueError("URL khong tra ve MJPEG multipart/x-mixed-replace.")
    boundary = response.headers.get_param("boundary")
    if not boundary or len(boundary) > 200:
        raise ValueError("Thieu boundary MJPEG hop le.")
    delimiter = b"--" + boundary.encode("ascii")
    max_line = 4096
    max_headers = 16384

    def read_line():
        if stop_event.is_set():
            raise _StreamStopped()
        line = response.readline(max_line + 1)
        if not line:
            raise EOFError("Server da dong luong MJPEG.")
        if len(line) > max_line:
            raise ValueError("Header MJPEG qua dai.")
        return line

    while not stop_event.is_set():
        used = 0
        line = read_line()
        while line in (b"\r\n", b"\n"):
            used += len(line)
            if used > max_headers:
                raise ValueError("Qua nhieu dong trong trong MJPEG.")
            line = read_line()
        marker = line.rstrip(b"\r\n")
        if marker == delimiter + b"--":
            return
        if marker != delimiter:
            raise ValueError("Boundary MJPEG khong hop le.")

        headers = {}
        used = 0
        while True:
            line = read_line()
            used += len(line)
            if used > max_headers:
                raise ValueError("Tong header MJPEG qua lon.")
            if line in (b"\r\n", b"\n"):
                break
            key, separator, value = line.partition(b":")
            if not separator:
                raise ValueError("Header MJPEG khong hop le.")
            headers[key.strip().lower()] = value.strip()

        if headers.get(b"content-type", b"").split(b";")[0].lower() != b"image/jpeg":
            raise ValueError("Mot phan trong luong khong phai JPEG.")
        try:
            length = int(headers.get(b"content-length", b""))
        except ValueError:
            raise ValueError("Thieu Content-Length JPEG hop le.")
        if length <= 0 or length > max_frame_bytes:
            raise ValueError("Kich thuoc JPEG vuot gioi han.")

        payload = bytearray()
        while len(payload) < length:
            if stop_event.is_set():
                raise _StreamStopped()
            chunk = response.read1(min(65536, length - len(payload)))
            if not chunk:
                raise EOFError("Luong bi ngat khi dang nhan mot JPEG.")
            payload.extend(chunk)
        jpeg = bytes(payload)
        if not jpeg.startswith(b"\xff\xd8") or not jpeg.endswith(b"\xff\xd9"):
            raise ValueError("Du lieu khung hinh khong phai JPEG hoan chinh.")
        yield jpeg


class LatestMjpegReader:
    """Mot ket noi MJPEG; chi giu JPEG moi nhat da nhan."""
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
            "mode": "starting", "message": "Dang khoi dong...",
            "jpeg": None, "sequence": 0, "received_at": 0.0,
            "connections": 0, "failures": 0,
        }

    def _update(self, **values):
        with self.lock:
            self.state.update(values)

    def latest_state(self):
        with self.lock:
            return dict(self.state)

    def start(self):
        self.thread = threading.Thread(
            target=self._run, name="jetracer-mjpeg-reader", daemon=True)
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
                self._update(mode="connecting", jpeg=None, received_at=0.0,
                             message="Dang ket noi /stream...")
                try:
                    request = Request(self.url, headers={
                        "Accept": "multipart/x-mixed-replace",
                        "Cache-Control": "no-cache",
                    })
                    # Chi mo mot lan cho ca luong; chi mo lai neu luong loi/ngat.
                    with urlopen(request, timeout=self.timeout) as response:
                        with self.lock:
                            self.state["connections"] += 1
                        self._update(mode="waiting",
                                     message="Da ket noi; dang cho JPEG dau tien.")
                        for jpeg in _mjpeg_parts(
                                response, self.stop_event, self.max_frame_bytes):
                            if self.stop_event.is_set():
                                break
                            failures = 0
                            with self.lock:
                                self.state.update(
                                    mode="streaming", message="Dang nhan MJPEG.",
                                    jpeg=jpeg, received_at=time.monotonic(),
                                    sequence=self.state["sequence"] + 1, failures=0)
                        if not self.stop_event.is_set():
                            raise EOFError("Server da ket thuc luong MJPEG.")
                except _StreamStopped:
                    break
                except Exception as exc:
                    if self.stop_event.is_set():
                        break
                    failures += 1
                    message = "{}: {}".format(type(exc).__name__, exc)
                    self._update(mode="retrying", jpeg=None, received_at=0.0,
                                 message=message, failures=failures)
                    if failures >= self.max_failures:
                        self._update(mode="failed")
                        break
                    if self.stop_event.wait(1.0):
                        break
        finally:
            # Response duoc chinh reader dong khi roi khoi 'with'.
            if self.stop_event.is_set():
                self._update(mode="stopped", jpeg=None, received_at=0.0,
                             message="Reader da dung.")


print("[3/4] Dang khoi tao bo nhan dien ArUco...", flush=True)
if not hasattr(cv2, "aruco"):
    raise RuntimeError(
        "OpenCV nay khong co cv2.aruco. Version: {} | File: {}"
        .format(cv2.__version__, cv2.__file__))
if (TARGET_FPS <= 0 or MAX_WIDTH < 0 or HTTP_TIMEOUT <= 0 or
        STALE_AFTER <= 0 or TEXT_UPDATE_SECONDS <= 0):
    raise ValueError("Cac moc thoi gian/FPS phai > 0; MAX_WIDTH phai >= 0.")

aruco = cv2.aruco
if not hasattr(aruco, ARUCO_DICT_NAME):
    raise ValueError("Dictionary khong ton tai: " + ARUCO_DICT_NAME)
dictionary_id = getattr(aruco, ARUCO_DICT_NAME)
dictionary = (aruco.getPredefinedDictionary(dictionary_id)
              if hasattr(aruco, "getPredefinedDictionary")
              else aruco.Dictionary_get(dictionary_id))
parameters = (aruco.DetectorParameters_create()
              if hasattr(aruco, "DetectorParameters_create")
              else aruco.DetectorParameters())
detector = (aruco.ArucoDetector(dictionary, parameters)
            if hasattr(aruco, "ArucoDetector") else None)
print("[OK] ArUco:", ARUCO_DICT_NAME, flush=True)

status_view = display(HTML("<pre>Dang chuan bi doc /stream...</pre>"), display_id=True)
if status_view is None:
    raise RuntimeError("Hay chay cell nay trong Jupyter Notebook/JupyterLab.")
_status_key = None
_status_updated_at = 0.0


def show_status(message, key, force=False):
    global _status_key, _status_updated_at
    now = time.monotonic()
    if (force or key != _status_key or
            now - _status_updated_at >= TEXT_UPDATE_SECONDS):
        status_view.update(HTML("<pre>" + escape(message) + "</pre>"))
        _status_key = key
        _status_updated_at = now


_aruco_mjpeg_reader = LatestMjpegReader(
    STREAM_URL, timeout=HTTP_TIMEOUT, max_failures=MAX_FAILURES)
detected_ids = []
processed_count = 0
last_sequence = 0
next_process_at = 0.0
processing_failures = 0
stop_reason = "DA DUNG QUET."
id0_latched = False
id0_absent_since = None

try:
    print("[4/4] Bat dau doc /stream va quet. Dung: Kernel > Interrupt Kernel.",
          flush=True)
    _aruco_mjpeg_reader.start()
    while True:
        state = _aruco_mjpeg_reader.latest_state()
        now = time.monotonic()
        fresh = (state["mode"] == "streaming" and state["jpeg"] is not None
                 and now - state["received_at"] <= STALE_AFTER)

        if not fresh:
            detected_ids = []
            next_process_at = 0.0
            message = state["message"]
            if state["mode"] == "streaming":
                message = "Chua nhan duoc JPEG moi; dang cho luong camera."
            show_status(
                "CHUA CO KET QUA HIEN TAI\n" + message,
                ("waiting", state["mode"], message))
            if state["mode"] in ("failed", "stopped"):
                stop_reason = "DA DUNG READER: " + message
                break
            if not _aruco_mjpeg_reader.thread.is_alive():
                stop_reason = "READER DA KET THUC: " + message
                break
            time.sleep(0.05)
            continue

        if state["sequence"] == last_sequence or now < next_process_at:
            time.sleep(0.02)
            continue
        last_sequence = state["sequence"]
        next_process_at = now + 1.0 / TARGET_FPS

        try:
            # Chi can anh xam trong bo nho de nhan dien; khong tao anh hien thi.
            gray = cv2.imdecode(
                np.frombuffer(state["jpeg"], dtype=np.uint8),
                cv2.IMREAD_GRAYSCALE)
            if gray is None:
                raise RuntimeError("Khong giai ma duoc JPEG trong luong.")
            if MAX_WIDTH and gray.shape[1] > MAX_WIDTH:
                scale = float(MAX_WIDTH) / gray.shape[1]
                height = max(1, int(round(gray.shape[0] * scale)))
                gray = cv2.resize(gray, (MAX_WIDTH, height),
                                  interpolation=cv2.INTER_AREA)
            if detector is not None:
                corners, ids, rejected = detector.detectMarkers(gray)
            else:
                corners, ids, rejected = aruco.detectMarkers(
                    gray, dictionary, parameters=parameters)
            current_ids = [] if ids is None else sorted(set(ids.flatten().tolist()))

            current_state = _aruco_mjpeg_reader.latest_state()
            if (current_state["mode"] != "streaming" or
                    current_state["connections"] != state["connections"] or
                    time.monotonic() - state["received_at"] > STALE_AFTER):
                detected_ids = []
                show_status(
                    "CHUA CO KET QUA HIEN TAI\n"
                    "Ket qua da cu hoac ket noi thay doi; dang cho khung moi.",
                    ("discarded",))
                continue

            processed_count += 1
            processing_failures = 0
            detected_ids = current_ids

            # ID 0 triggers exactly one circle per marker appearance.
            if TRIGGER_ARUCO_ID in detected_ids:
                id0_absent_since = None
                if not id0_latched:
                    id0_latched = True
                    if start_circle_if_idle():
                        print(
                            "[TRIGGER] ArUco ID {} -> start 30s circle"
                            .format(TRIGGER_ARUCO_ID),
                            flush=True)
            elif id0_latched:
                if id0_absent_since is None:
                    id0_absent_since = now
                elif (not _movement_active.is_set() and
                      now - id0_absent_since >= REARM_ABSENT_SECONDS):
                    id0_latched = False
                    id0_absent_since = None
                    print(
                        "[TRIGGER] ID {} absent -> trigger re-armed"
                        .format(TRIGGER_ARUCO_ID),
                        flush=True)

            if detected_ids:
                headline = "DA NHAN DIEN ARUCO - ID: " + ", ".join(
                    str(marker_id) for marker_id in detected_ids)
            else:
                headline = "DANG QUET - Chua phat hien ArUco."

            movement_text = (
                "DANG CHAY VONG TRON 30s"
                if _movement_active.is_set() else
                "Robot dang dung / cho ID 0")
            status_text = (
                "{}\n{}\nDa xu ly: {} khung | Cap nhat: {}"
                .format(headline, movement_text, processed_count,
                        time.strftime("%H:%M:%S")))
            show_status(
                status_text,
                ("result", tuple(detected_ids), _movement_active.is_set()))

        except (RuntimeError, cv2.error) as exc:
            processing_failures += 1
            detected_ids = []
            message = "LOI XU LY {}/{}: {}: {}".format(
                processing_failures, MAX_FAILURES, type(exc).__name__, exc)
            show_status(message, ("processing_error", message))
            if processing_failures >= MAX_FAILURES:
                stop_reason = "DA DUNG QUET. " + message
                break

except KeyboardInterrupt:
    stop_reason = "DA DUNG QUET THEO YEU CAU."
finally:
    # Important: interrupting/stopping the notebook also stops any active motion.
    cancel_circle_and_stop()
    reader_stopped = _aruco_mjpeg_reader.stop()
    if not reader_stopped:
        stop_reason += "\nReader van dang hoan tat viec dong ket noi."
    last_ids_before_stop = list(detected_ids)
    detected_ids = []
    show_status(
        "{}\nID o khung xu ly cuoi truoc khi dung: {}\nTong khung da xu ly: {}"
        .format(stop_reason, last_ids_before_stop, processed_count),
        ("stopped",), force=True)
    print(stop_reason, flush=True)

