"""SmartFleet stand-alone ArUco -> MQTT position scanner for a JetRacer Jupyter kernel.

No ROS commands, no motors, no automatic movement.

Usage (Jupyter):
    1. Paste this WHOLE file into a cell and run it (defines functions only).
    2. Run: publish_marker(1)           # optional synthetic backend smoke test
    3. Run: scan_camera()               # show real DICT_4X4_50 markers to camera
       Interrupt the cell to stop scanning; then run stop_mqtt().

Do not run the separate scanner at the same time as the full robot main_code.py
while testing the same camera and robot MQTT identity.
"""
import os
import platform
import sys
import time
import json
import uuid
from urllib.request import urlopen

if platform.machine().lower() in ("aarch64", "arm64"):
    os.environ["OPENBLAS_CORETYPE"] = "ARMV8"

ARUCO_DEPS_DIR = os.path.realpath(os.path.expanduser("~/jetracer_aruco_py36"))
if os.path.isdir(ARUCO_DEPS_DIR) and ARUCO_DEPS_DIR not in sys.path:
    sys.path.insert(0, ARUCO_DEPS_DIR)

import cv2
import numpy as np
import paho.mqtt.client as mqtt

# IMPORTANT: use SAME broker and robot code as SmartFleetBE and the Robots DB table.
BROKER_IP = "192.168.137.1"
BROKER_PORT = 1883
ROBOT_CODE = "R01"
STREAM_URL = "http://127.0.0.1:8080/stream?topic=/csi_cam_0/image_raw&type=mjpeg"
ARUCO_DICTIONARY = "DICT_4X4_50"
SCAN_FPS = 5.0
MAX_FRAME_WIDTH = 800
MARKER_HEARTBEAT_SECONDS = 5.0
MIN_MARKER_AREA_PIXELS = 100.0

POSITION_TOPIC = "smartfleet/robot/{}/aruco".format(ROBOT_CODE)
_map_mqtt = None
_map_last_id = None
_map_last_sent_at = 0.0


def _mqtt_client():
    global _map_mqtt
    if _map_mqtt is not None and _map_mqtt.is_connected():
        return _map_mqtt
    if _map_mqtt is not None:
        try:
            _map_mqtt.loop_stop()
            _map_mqtt.disconnect()
        except Exception:
            pass
    # Unique client ID prevents a collision with main_code.py or other notebooks.
    client_id = "smartfleet-map-{}-{}".format(ROBOT_CODE.lower(), uuid.uuid4().hex[:8])
    c = mqtt.Client(client_id=client_id)
    c.connect(BROKER_IP, BROKER_PORT, 60)
    c.loop_start()
    for _ in range(30):
        if c.is_connected():
            _map_mqtt = c
            print("[MQTT] Connected; topic:", POSITION_TOPIC, flush=True)
            return c
        time.sleep(0.1)
    c.loop_stop()
    c.disconnect()
    raise RuntimeError("Could not connect to MQTT broker {}:{}".format(BROKER_IP, BROKER_PORT))


def publish_marker(aruco_id):
    """Send ONE position observation. Does not command robot movement."""
    marker_id = int(aruco_id)
    if marker_id < 0 or marker_id > 9999:
        raise ValueError("ArUco marker ID must be between 0 and 9999")
    client = _mqtt_client()
    payload = json.dumps({"arucoId": marker_id})
    info = client.publish(POSITION_TOPIC, payload, qos=1, retain=False)
    if info.rc != mqtt.MQTT_ERR_SUCCESS:
        raise RuntimeError("MQTT publish failed: {}".format(info.rc))
    info.wait_for_publish(timeout=5)
    if not info.is_published():
        raise RuntimeError("MQTT publish timed out")
    print("[MAP] Sent ArUco ID {} -> BE (BE resolves grid cell)".format(marker_id), flush=True)


def stop_mqtt():
    global _map_mqtt
    if _map_mqtt is not None:
        try:
            _map_mqtt.loop_stop()
            _map_mqtt.disconnect()
        finally:
            _map_mqtt = None
            print("[MQTT] Stopped", flush=True)


def _mjpeg_jpegs(stream):
    buf = bytearray()
    while True:
        chunk = stream.read(4096)
        if not chunk:
            raise IOError("Camera MJPEG stream ended")
        buf.extend(chunk)
        start = buf.find(b"\xff\xd8")
        if start >= 0:
            end = buf.find(b"\xff\xd9", start + 2)
            if end >= 0:
                jpeg = bytes(buf[start:end + 2])
                del buf[:end + 2]
                yield jpeg
                continue
        # Guard against a broken stream or missing frame boundaries.
        if len(buf) > 5_000_000:
            del buf[:-65536]


def _aruco_detector():
    if not hasattr(cv2, "aruco"):
        raise RuntimeError("cv2.aruco not found. Use your existing JetRacer OpenCV contrib environment.")
    a = cv2.aruco
    dictionary_code = getattr(a, ARUCO_DICTIONARY)
    dictionary = (a.getPredefinedDictionary(dictionary_code)
                  if hasattr(a, "getPredefinedDictionary")
                  else a.Dictionary_get(dictionary_code))
    params = (a.DetectorParameters_create()
              if hasattr(a, "DetectorParameters_create")
              else a.DetectorParameters())
    detector = a.ArucoDetector(dictionary, params) if hasattr(a, "ArucoDetector") else None
    return a, dictionary, params, detector


def scan_camera():
    """Block in Jupyter and publish the visually LARGEST valid marker every 5 seconds.

    The icon is a DISCRETE last-marker location, not robot pose estimation.
    Keyboard interrupt safely closes camera stream; no motors are touched.
    """
    global _map_last_id, _map_last_sent_at
    _mqtt_client()
    a, dictionary, params, detector = _aruco_detector()
    next_scan_at = 0.0
    last_print_at = 0.0
    print("[CAMERA] Starting real ArUco detection. Press Interrupt to stop.", flush=True)
    try:
        with urlopen(STREAM_URL, timeout=5) as stream:
            for jpeg in _mjpeg_jpegs(stream):
                now = time.monotonic()
                if now < next_scan_at:
                    continue
                next_scan_at = now + 1.0 / SCAN_FPS
                frame = cv2.imdecode(np.frombuffer(jpeg, dtype=np.uint8), cv2.IMREAD_GRAYSCALE)
                if frame is None:
                    continue
                if MAX_FRAME_WIDTH and frame.shape[1] > MAX_FRAME_WIDTH:
                    new_h = int(frame.shape[0] * float(MAX_FRAME_WIDTH) / frame.shape[1])
                    frame = cv2.resize(frame, (MAX_FRAME_WIDTH, max(1, new_h)))
                if detector is not None:
                    corners, ids, _ = detector.detectMarkers(frame)
                else:
                    corners, ids, _ = a.detectMarkers(frame, dictionary, parameters=params)

                if ids is None or len(ids) == 0:
                    if now - last_print_at >= 5:
                        print("[SCAN] No marker visible", flush=True)
                        last_print_at = now
                    continue

                # If the camera sees several markers, choose the largest in the image.
                candidates = []
                for item_id, corner in zip(ids.flatten().tolist(), corners):
                    poly = np.asarray(corner, dtype=np.float32).reshape(4, 2)
                    area = abs(cv2.contourArea(poly))
                    if area >= MIN_MARKER_AREA_PIXELS:
                        candidates.append((area, int(item_id)))
                if not candidates:
                    continue
                _, chosen_id = max(candidates)
                if chosen_id != _map_last_id or now - _map_last_sent_at >= MARKER_HEARTBEAT_SECONDS:
                    publish_marker(chosen_id)
                    _map_last_id = chosen_id
                    _map_last_sent_at = now
    except KeyboardInterrupt:
        print("[CAMERA] Stopped by user", flush=True)
    finally:
        print("[CAMERA] Stream closed. Run stop_mqtt() when finished.", flush=True)


print("Ready. First run publish_marker(1), then scan_camera().", flush=True)
