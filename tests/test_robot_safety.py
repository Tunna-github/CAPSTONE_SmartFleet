"""Run without Webots/ROS: executes actual safety functions with fake motor/MQTT IO."""
import ast
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock
import uuid

ROOT = Path(__file__).resolve().parents[1]

class RobotSafetyTests(unittest.TestCase):
    def setUp(self):
        tree = ast.parse((ROOT / "Webots_Full_Mission_Controller/my_controller_testing_fleet.py").read_text(encoding="utf-8-sig"))
        names = {"latch_safety", "safety_ack", "handle_safety_command", "finish_home", "tick_mission"}
        code = ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in names], type_ignores=[])
        self.temp = tempfile.TemporaryDirectory()
        self.g = dict(os=os, json=json, uuid=uuid, SAFETY_FILE=os.path.join(self.temp.name, "hold.json"),
            safety_hold=False, safety_commands={}, safety_generation={}, mission={"missionId": 3, "phase": "MOVING"},
            current_marker=0, current_state="MOVING", STATE_IDLE="IDLE", pending=None, INTERSECTION_PILOT=False,
            STATUS_TOPIC="status", STATUS_ACK_TOPIC="ack", stop_robot=Mock(),
            publish=Mock(), send_reliable=Mock(), visible_marker=Mock(return_value=object()),
            valid_int=lambda x: type(x) is int, expected_grid_direction=lambda a,b: "E", begin_segment=Mock())
        exec(compile(code, "controller-safety", "exec"), self.g)
        self.cmd = dict(command="PAUSE_TASK", commandId="one", generation=1, taskId=1, assignmentId=2, missionId=3)

    def tearDown(self):
        self.temp.cleanup()

    def test_pause_stops_preserves_mission_and_writes_persistent_latch(self):
        self.g["handle_safety_command"](self.cmd)
        self.assertTrue(self.g["safety_hold"])
        self.assertEqual(self.g["mission"]["missionId"], 3)
        self.assertTrue(Path(self.g["SAFETY_FILE"]).exists())
        self.g["stop_robot"].assert_called()
        self.assertEqual(self.g["publish"].call_args.args[1]["commandId"], "one")

    def test_stale_return_home_cannot_override_newer_pause(self):
        self.g["handle_safety_command"](dict(self.cmd, generation=5))
        self.g["handle_safety_command"](dict(self.cmd, command="RETURN_HOME", commandId="stale", generation=4))
        self.assertTrue(self.g["safety_hold"])
        self.g["begin_segment"].assert_not_called()

    def test_no_fresh_qr_does_not_start_home_route(self):
        self.g["handle_safety_command"](self.cmd)
        self.g["visible_marker"].return_value = None
        route = dict(startArUcoId=0, homeArUcoId=1, waypoints=[dict(arucoId=0), dict(arucoId=1, travelDirection="E")])
        self.g["handle_safety_command"](dict(self.cmd, command="RETURN_HOME", commandId="home", generation=2, route=route))
        self.assertTrue(self.g["safety_hold"])
        self.g["begin_segment"].assert_not_called()

    def test_home_route_starts_only_from_fresh_camera_qr(self):
        self.g["handle_safety_command"](self.cmd)
        route = dict(startArUcoId=0, homeArUcoId=1, waypoints=[dict(arucoId=0), dict(arucoId=1, travelDirection="E")])
        data = dict(self.cmd, command="RETURN_HOME", commandId="home", generation=2, route=route)
        self.g["handle_safety_command"](data)
        self.g["begin_segment"].assert_called_once()
        self.assertFalse(self.g["safety_hold"])
        self.assertTrue(Path(self.g["SAFETY_FILE"]).exists())
        self.g["handle_safety_command"](data)
        self.g["begin_segment"].assert_called_once()  # MQTT retry does not restart motion

    def test_paused_tick_never_runs_motion(self):
        self.g["handle_safety_command"](self.cmd)
        self.g["tick_mission"]()
        self.g["begin_segment"].assert_not_called()

    def test_home_arrival_keeps_latch_until_backend_ack(self):
        self.g["handle_safety_command"](self.cmd)
        data = dict(self.cmd, home_command_id="home", phase="MOVING")
        self.g["mission"] = data
        self.g["finish_home"](data)
        self.assertTrue(self.g["safety_hold"])
        self.assertEqual(data["phase"], "HOME_ACK")
        self.assertEqual(self.g["send_reliable"].call_args.args[0], "HOME_REACHED")
        self.g.update(connected=True, tick_pending=lambda: (True, "Applied", "HOME_REACHED", None))
        self.g["tick_mission"]()
        self.assertFalse(self.g["safety_hold"])
        self.assertIsNone(self.g["mission"])
        self.assertFalse(Path(self.g["SAFETY_FILE"]).exists())

if __name__ == "__main__":
    unittest.main()
