"""Camera pose in MAP_N_W_UP, with a session-only IMU yaw alignment.

No Webots ground truth, start QR, previous heading, or motion is used to initialize.
Camera mounting and marker orientation are installation calibration, not robot pose.
"""
import math
import cv2
import numpy as np


def wrap_degrees(value):
    return (float(value) + 180.0) % 360.0 - 180.0


def rotation_z(degrees):
    a = math.radians(degrees)
    return np.array([[math.cos(a), -math.sin(a), 0.0],
                     [math.sin(a), math.cos(a), 0.0], [0.0, 0.0, 1.0]])


class PoseEstimator:
    def __init__(self, calibration, width, height, horizontal_fov):
        if calibration.get("schemaVersion") != 1 or calibration.get("frame") != "MAP_N_W_UP":
            raise ValueError("Missing/unsupported camera installation calibration")
        if calibration.get("imageWidth") != width or calibration.get("imageHeight") != height:
            raise ValueError("Camera resolution differs from installation calibration")
        if abs(float(calibration["horizontalFovRad"]) - horizontal_fov) > 0.0001:
            raise ValueError("Camera field of view changed; recalibrate")
        focal = width / (2.0 * math.tan(horizontal_fov / 2.0))
        self.camera_matrix = np.array(calibration.get("cameraMatrix", [
            [focal, 0.0, (width - 1) / 2.0], [0.0, focal, (height - 1) / 2.0], [0.0, 0.0, 1.0]]), dtype=float)
        self.distortion = np.array(calibration.get("distortionCoefficients", [0.0] * 5), dtype=float)
        self.robot_from_optical = np.array(calibration["robotFromOpticalRotation"], dtype=float)
        if (self.robot_from_optical.shape != (3, 3) or not np.isfinite(self.robot_from_optical).all()
                or not np.allclose(self.robot_from_optical.T @ self.robot_from_optical, np.eye(3), atol=1e-5)
                or not np.isclose(np.linalg.det(self.robot_from_optical), 1.0, atol=1e-5)):
            raise ValueError("Camera mounting rotation is not a proper rotation")
        self.max_error = 2.0

    def estimate(self, corners, marker):
        size = marker.get("markerSizeM")
        yaw = marker.get("markerYawDeg")
        if size is None or yaw is None or not math.isfinite(float(yaw)) or not 0.005 <= float(size) <= 2.0:
            raise ValueError("Marker size/orientation has not been commissioned on the map")
        points = np.asarray(corners, dtype=np.float64).reshape(4, 2)
        if not np.isfinite(points).all() or min(np.linalg.norm(points[(i + 1) % 4] - points[i]) for i in range(4)) < 14:
            raise ValueError("Marker image is too small or invalid for heading estimation")
        s = float(size) / 2.0
        model = np.array([[-s, s, 0.0], [s, s, 0.0], [s, -s, 0.0], [-s, -s, 0.0]])
        result = cv2.solvePnPGeneric(model, points, self.camera_matrix, self.distortion,
                                     flags=cv2.SOLVEPNP_IPPE_SQUARE)
        candidates = []
        for rvec, tvec in zip(result[1], result[2]):
            if not np.isfinite(rvec).all() or not np.isfinite(tvec).all() or tvec[2, 0] <= 0:
                continue
            camera_from_marker = cv2.Rodrigues(rvec)[0]
            map_from_robot = rotation_z(float(yaw)) @ camera_from_marker.T @ self.robot_from_optical.T
            # An upside-down/mirrored pose cannot explain a wheeled robot on this floor.
            if map_from_robot[2, 2] < math.cos(math.radians(15.0)):
                continue
            projected = cv2.projectPoints(model, rvec, tvec, self.camera_matrix, self.distortion)[0].reshape(4, 2)
            error = float(np.sqrt(np.mean(np.sum((projected - points) ** 2, axis=1))))
            if error > self.max_error:
                continue
            angle = wrap_degrees(math.degrees(math.atan2(map_from_robot[1, 0], map_from_robot[0, 0])))
            candidates.append({"headingDeg": angle, "reprojectionErrorPx": error})
        if not candidates:
            raise ValueError("No camera pose passes the projection/gravity checks")
        candidates.sort(key=lambda p: p["reprojectionErrorPx"])
        best = candidates[0]
        if any(p["reprojectionErrorPx"] <= best["reprojectionErrorPx"] + 0.3 and
               abs(wrap_degrees(p["headingDeg"] - best["headingDeg"])) > 8.0 for p in candidates[1:]):
            raise ValueError("Ambiguous planar marker orientation; hold for a clearer view")
        return best


class HeadingFusion:
    def __init__(self, stable_frames=5):
        self.stable_frames = stable_frames
        self.reset()

    def reset(self):
        self.marker_id = None
        self.samples = []
        self.offset = None
        self.last_error = None
        self.last_vision_yaw = None

    def observe(self, marker_id, vision_yaw, imu_yaw, reprojection_error):
        if not all(math.isfinite(float(x)) for x in (vision_yaw, imu_yaw, reprojection_error)):
            self.reset()
            return False
        offset = wrap_degrees(imu_yaw - vision_yaw)
        if self.marker_id != marker_id or (self.samples and
                (abs(wrap_degrees(offset - self.samples[-1])) > 3.0 or
                 abs(wrap_degrees(vision_yaw - self.last_vision_yaw)) > 2.0)):
            self.marker_id, self.samples = marker_id, []
        self.last_vision_yaw = vision_yaw
        self.samples = (self.samples + [offset])[-self.stable_frames:]
        self.last_error = reprojection_error
        if len(self.samples) < self.stable_frames:
            return False
        radians = np.radians(self.samples)
        self.offset = wrap_degrees(math.degrees(math.atan2(float(np.sin(radians).mean()), float(np.cos(radians).mean()))))
        return True

    def heading(self, imu_yaw):
        if self.offset is None or not math.isfinite(float(imu_yaw)):
            raise ValueError("Orientation has not been localized in this IMU session")
        return wrap_degrees(imu_yaw - self.offset)
