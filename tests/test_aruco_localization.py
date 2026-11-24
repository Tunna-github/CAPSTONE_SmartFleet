"""Exercises actual PnP with projected and detected markers; no Webots ground truth in runtime."""
import importlib.util
import ast
import json
import math
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock
import time
import uuid
import cv2
import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "Webots_Full_Mission_Controller"))
from aruco_localization import PoseEstimator, HeadingFusion, rotation_z, wrap_degrees


class LocalizationTests(unittest.TestCase):
    def setUp(self):
        self.calibration = json.loads((ROOT / "Webots_Full_Mission_Controller/aruco_camera_calibration.json").read_text())
        self.estimator = PoseEstimator(self.calibration, 626, 250, 0.85404)
        self.marker = {"arucoId": 4, "markerYawDeg": -90.0, "markerSizeM": 0.04}

    def corners(self, yaw):
        robot_rotation = rotation_z(yaw) @ cv2.Rodrigues(np.array([0.004, -0.003, 0.0]))[0]
        camera_rotation = robot_rotation @ self.estimator.robot_from_optical
        relative = camera_rotation.T @ rotation_z(self.marker["markerYawDeg"])
        translation = camera_rotation.T @ np.array([[0.0], [0.0], [-0.244]])
        s = 0.02
        model = np.array([[-s, s, 0], [s, s, 0], [s, -s, 0], [-s, -s, 0]], dtype=float)
        return cv2.projectPoints(model, cv2.Rodrigues(relative)[0], translation,
                                 self.estimator.camera_matrix, self.estimator.distortion)[0].reshape(4, 2)

    def test_actual_pose_math_distinguishes_all_headings_including_180(self):
        for angle in (-180, -135, -90, -45, 0, 45, 90, 135, 179):
            with self.subTest(angle=angle):
                pose = self.estimator.estimate(self.corners(angle), self.marker)
                self.assertLess(abs(wrap_degrees(pose["headingDeg"] - angle)), 0.2)

    def test_detected_pixels_keep_marker_corner_order_after_rotating_180(self):
        dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
        detector = cv2.aruco.ArucoDetector(dictionary)
        image = cv2.aruco.generateImageMarker(dictionary, 4, 256)
        source = np.float32([[0, 0], [255, 0], [255, 255], [0, 255]])
        for angle in (0, 90, 180, -90, 45):
            target = self.corners(angle).astype(np.float32)
            frame = cv2.warpPerspective(image, cv2.getPerspectiveTransform(source, target), (626, 250),
                                        borderValue=255, flags=cv2.INTER_NEAREST)
            corners, ids, _ = detector.detectMarkers(frame)
            self.assertEqual(ids.flatten().tolist(), [4])
            pose = self.estimator.estimate(corners[0][0], self.marker)
            self.assertLess(abs(wrap_degrees(pose["headingDeg"] - angle)), 2.0)

    def test_new_imu_session_may_have_arbitrary_zero(self):
        for initial_imu in (0, 90, -179):
            fusion = HeadingFusion()
            for _ in range(5): ready = fusion.observe(4, -90, initial_imu, 0.1)
            self.assertTrue(ready)
            self.assertAlmostEqual(fusion.heading(initial_imu), -90)
            self.assertAlmostEqual(fusion.heading(wrap_degrees(initial_imu + 45)), -45)

    def test_a_single_frame_or_unstable_pose_cannot_be_ready(self):
        fusion = HeadingFusion()
        self.assertFalse(fusion.observe(4, 0, 0, 0.1))
        for angle in (20, 40, 60, 80):
            self.assertFalse(fusion.observe(4, angle, angle, 0.1))
        fusion.reset()
        with self.assertRaises(ValueError): fusion.heading(0)

    def test_missing_metadata_and_tiny_marker_hold(self):
        with self.assertRaises(ValueError): self.estimator.estimate(self.corners(0), {"arucoId": 4})
        with self.assertRaises(ValueError): self.estimator.estimate(np.array([[1, 1], [2, 1], [2, 2], [1, 2]]), self.marker)

    def test_changed_camera_installation_requires_recalibration(self):
        with self.assertRaises(ValueError): PoseEstimator(self.calibration, 640, 480, 0.85404)
        with self.assertRaises(ValueError): PoseEstimator(self.calibration, 626, 250, 1.0)

    def test_wrap_boundary_does_not_average_opposite_heading(self):
        fusion = HeadingFusion()
        for imu in (179.9, -179.9, 179.8, -179.8, 180): fusion.observe(4, 0, imu, 0.1)
        self.assertLess(abs(wrap_degrees(fusion.heading(180))), 0.3)

class ControllerLocalizationTests(unittest.TestCase):
    def fixture(self):
        tree = ast.parse((ROOT / "Webots_Full_Mission_Controller/my_controller_testing_fleet.py").read_text(encoding="utf-8-sig"))
        names = {"invalidate_localization", "tick_localization", "decide_turn"}
        code = ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names], type_ignores=[])
        fusion = HeadingFusion()
        for _ in range(5): fusion.observe(0, 0, 90, 0.1)
        g = dict(mission=None, connected=True, localization_valid=True, localization_map={"mapRevision": 5,
            "markers": [{"arucoId": 0, "markerSizeM": 0.04, "markerYawDeg": -90}]},
            localization_reconnect=False, localization_last_frame_at=-100, localization_last_warning_at=time.monotonic(),
            localization_diagnostics_dir="", localization_reason="", pose_estimator=Mock(), heading_fusion=fusion,
            robot=Mock(getTime=Mock(return_value=2)), visible_any_marker=Mock(return_value=None),
            stop_robot=Mock(), request_localization_map=Mock(), publish=Mock(), send_ready=Mock(),
            ready_event_id="old", ready_acknowledged=True, current_marker=0, is_first_checkin=False,
            cv2=cv2, time=time, uuid=uuid, get_yaw_deg=Mock(return_value=90),
            normalize_angle=wrap_degrees, DIRECTION_YAW={"N":0,"W":90,"S":180,"E":-90},
            current_heading="E", base_yaw_offset=90, ROBOT_CODE="R01", QR_PROGRESS_TOPIC="qr-progress")
        exec(compile(code, "actual-controller-localization", "exec"), g)
        return g

    def test_losing_marker_revokes_ready_and_stops(self):
        g = self.fixture()
        g["tick_localization"](force=True)
        self.assertFalse(g["localization_valid"])
        self.assertFalse(g["ready_acknowledged"])
        self.assertIsNone(g["current_marker"])
        g["stop_robot"].assert_called()
        g["send_ready"].assert_not_called()
        self.assertEqual(g["publish"].call_args.args[1]["event"], "LOCALIZATION_LOST")

    def test_ambiguous_pose_cannot_keep_old_heading_ready(self):
        g = self.fixture()
        g["visible_any_marker"].return_value = (0, [np.zeros((4, 2))], 626)
        g["pose_estimator"].estimate.side_effect = ValueError("ambiguous")
        g["tick_localization"](force=True)
        self.assertFalse(g["localization_valid"])
        g["send_ready"].assert_not_called()

    def test_missing_camera_calibration_holds_instead_of_using_a_manual_heading(self):
        g = self.fixture()
        g["pose_estimator"] = None
        g["tick_localization"](force=True)
        self.assertFalse(g["localization_valid"])
        g["send_ready"].assert_not_called()

    def test_first_turn_uses_measured_angle_instead_of_previous_cardinal_label(self):
        g = self.fixture()
        g["get_yaw_deg"].return_value = 45  # IMU offset 90 -> actual map heading -45
        self.assertEqual(g["decide_turn"]("N"), (45.0, "LEFT"))
        g["localization_valid"] = False
        with self.assertRaises(ValueError): g["decide_turn"]("S")


if __name__ == "__main__": unittest.main()
