import ast
import os
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from mqtt_hotspot import compose_files, keep_hotspot_overlay, endpoint, OVERLAY
from check_mqtt_hotspot import responder_source


class MqttHotspotTests(unittest.TestCase):
    def test_enabling_is_idempotent_and_retains_all_database_overrides(self):
        original=['compose.yaml','compose.charging.yaml','compose.aiven.yaml']
        first=compose_files(dict(COMPOSE_FILE=os.pathsep.join(original)))
        self.assertEqual(original+[OVERLAY],first)
        self.assertEqual(first,compose_files(dict(COMPOSE_FILE=os.pathsep.join(first))))

    def test_aiven_and_layout_setup_preserve_explicit_hotspot_choice(self):
        config=dict(COMPOSE_FILE='compose.yaml;compose.aiven.yaml;'+OVERLAY,COMPOSE_PATH_SEPARATOR=';')
        self.assertEqual(['compose.yaml',OVERLAY],keep_hotspot_overlay(['compose.yaml'],config))
        self.assertEqual(['compose.yaml'],keep_hotspot_overlay(['compose.yaml'],{}))

    def test_refuses_all_interface_or_non_lan_binding(self):
        for host in ('0.0.0.0','127.0.0.1','169.254.1.2','8.8.8.8','::1'):
            with self.assertRaises(ValueError):endpoint(host,1883)
        self.assertEqual(('192.168.137.1',1883),endpoint('192.168.137.1',1883))

    def test_diagnostic_uses_no_motion_protocol_or_hardware_libraries(self):
        source=responder_source('192.168.137.1',1883,'smartfleet/diagnostics/hotspot/test','nonce')
        ast.parse(source,feature_version=(3,6))
        for forbidden in ('EXECUTE_TASK','cmd_vel','JetRacer','cv2','/status','/command'):
            self.assertNotIn(forbidden,source)
        self.assertIn('retain=False',source)


if __name__=='__main__':unittest.main()
