"""Exercise JetRacer's actual command gates without loading ROS or touching hardware."""
import ast
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import Mock
import uuid


class PhysicalSafetyTests(unittest.TestCase):
    def setUp(self):
        source = Path(__file__).resolve().parents[1] / "Robot code/main_code.py"
        tree = ast.parse(source.read_text(encoding="utf-8-sig"))
        nodes = [n for n in tree.body if isinstance(n, (ast.ClassDef, ast.FunctionDef)) and n.name in {"CmdVelBridge", "send_status"}]
        self.connected, self.hold = threading.Event(), threading.Event()
        self.connected.set()
        self.g = dict(mqtt_connected=self.connected, safety_hold=self.hold, json=json, uuid=uuid,
                      STATUS_TOPIC="test", ack_lock=threading.Lock(), pending_acks={}, threading=threading,
                      mqtt=Mock(MQTT_ERR_SUCCESS=0), client=Mock(), print=Mock())
        exec(compile(ast.Module(body=nodes, type_ignores=[]), str(source), "exec"), self.g)

    def test_control_ack_does_not_block_mqtt_callback(self):
        result = self.g["client"].publish.return_value
        result.rc = 0
        self.g["send_status"]("CONTROL_STOPPED", 1, 2, 3, wait_ack=False, command_id="control")
        result.wait_for_publish.assert_not_called()
        self.g["client"].publish.assert_called_once()

    def test_disconnect_stops_motion_and_rejects_next_velocity(self):
        bridge = self.g["CmdVelBridge"]()
        bridge.proc = Mock(); bridge.proc.poll.return_value = None
        self.connected.clear()
        with self.assertRaisesRegex(RuntimeError, "MQTT connection was lost"):
            bridge.command(0.18, 0.0)
        bridge.proc.stdin.write.assert_called_once_with(b"0.000000 0.000000\n")

    def test_pause_latch_blocks_velocity_until_physical_recovery(self):
        bridge = self.g["CmdVelBridge"]()
        bridge.proc = Mock(); bridge.proc.poll.return_value = None
        self.hold.set()
        with self.assertRaisesRegex(RuntimeError, "safety hold"):
            bridge.command(0.18, 0.0)
        bridge.proc.stdin.write.assert_not_called()


if __name__ == "__main__":
    unittest.main()
