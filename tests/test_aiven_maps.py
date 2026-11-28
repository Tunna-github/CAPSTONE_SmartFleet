import sys
import os
from pathlib import Path
import unittest
import subprocess
import tempfile
import shutil
import json
import copy
from unittest.mock import patch
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from aiven_support import map_matches_scene
from configure_aiven import parse_connection_string
from select_aiven_map import select_sql
from map_switch_state import finish, ownership_sql
from map_switch_worker import run_job


class AivenMapTests(unittest.TestCase):
    def test_switch_keeps_independent_warehouse_station_rows(self):
        text=select_sql('map1', [2], 1, source_warehouse=1, target_warehouse=2)
        self.assertNotIn('DELETE FROM "StationCheckpoints"',text)
        self.assertNotIn('UPDATE "Stations"',text)
        self.assertNotIn('UPDATE "MapConfigurations"',text)
        self.assertIn('SET "WarehouseID"=2',text)
        self.assertIn('"IsSelected"=true',text)
    def test_unsafe_transition_failure_keeps_dispatch_gate(self):
        job = {'id': 'a0a06615-8331-452c-a24b-4100e5fbb7b3', 'target': 'map1'}
        for stage, outcome in [('BACKING_UP', 'FAILED'), ('STOPPING_WORLD', 'FAILED_HOLD'), ('VERIFYING', 'FAILED_HOLD')]:
            with tempfile.TemporaryDirectory() as folder, patch('map_switch_worker.private_dir', return_value=Path(folder)), \
                patch('map_switch_worker.subprocess.run', return_value=SimpleNamespace(returncode=1)), \
                patch('map_switch_worker.sql', return_value=stage), patch('map_switch_worker.finish') as completed:
                run_job(job)
                self.assertEqual(outcome, completed.call_args.args[1])

    def test_recovery_hold_never_unlocks_dispatch(self):
        with patch('map_switch_state.sql') as execute:
            finish('a0a06615-8331-452c-a24b-4100e5fbb7b3', 'FAILED_HOLD', 'recover')
            self.assertNotIn('SET "ActiveRequestId"=NULL', execute.call_args.args[0])
        with self.assertRaises(ValueError): ownership_sql('not-a-uuid;drop')

    def test_connection_parser_preserves_quoted_password_and_certificate(self):
        value = parse_connection_string('Host=server;Password="a;b""c";Database=shared;Root Certificate="C:/private/ca.pem";')
        self.assertEqual('a;b"c', value['password'])
        self.assertEqual('C:/private/ca.pem', value['root certificate'])
        self.assertEqual('shared', value['database'])

    def test_malformed_credentials_are_rejected(self):
        with self.assertRaises(ValueError): parse_connection_string('Password="unfinished')

    def test_unknown_map_never_builds_sql(self):
        with self.assertRaises(ValueError): select_sql("map2';drop table", [], 3)

    def test_old_setup_command_does_not_silently_revert_shared_database(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); (root / 'tools').mkdir()
            for name in ('setup_demo.py','aiven_support.py','mqtt_hotspot.py'):
                shutil.copyfile(Path(__file__).resolve().parents[1] / 'tools' / name, root / 'tools' / name)
            (root / '.env').write_text('AIVEN_CONNECTION_STRING=private-placeholder\nJWT_KEY=keep-this\nCOMPOSE_FILE=compose.yaml'+os.pathsep+'compose.mqtt-hotspot.yaml\n')
            subprocess.run([sys.executable, str(root / 'tools/setup_demo.py'), '--charging-layout'], check=True, capture_output=True)
            text = (root / '.env').read_text()
            self.assertIn('compose.aiven.yaml', text); self.assertIn('JWT_KEY=keep-this', text)
            self.assertIn('compose.mqtt-hotspot.yaml',text)

    def test_switch_is_transactional_and_retains_business_history(self):
        text = select_sql('map1', [2], 3)
        self.assertTrue(text.startswith('BEGIN;'))
        self.assertTrue(text.endswith('COMMIT;'))
        self.assertIn('pg_try_advisory_xact_lock', text)
        self.assertIn('"StopAcknowledgedAt" IS NULL', text)
        self.assertIn("'ACTIVE'", text)
        for table in ['TransportTasks', 'Missions', 'Users', 'DeliveryVerifications', 'TaskAssignments']:
            self.assertNotIn('DELETE FROM "' + table + '"', text)

    def test_target_scene_checks_calibration_roles_and_directed_graph(self):
        root = Path(__file__).resolve().parents[1]
        m = json.loads((root / 'simulation/config/dual-lane-map.json').read_text())
        scene = json.loads((root / 'simulation/config/dual-lane-manifest.json').read_text())
        self.assertTrue(map_matches_scene(m, scene))
        changed = copy.deepcopy(m); changed['links'][0].reverse()
        self.assertFalse(map_matches_scene(changed, scene))
        changed = copy.deepcopy(m)
        cell = next(c for row in changed['cells'] for c in row if c.get('arucoId') == 1)
        cell['markerYawDeg'] += 90
        self.assertFalse(map_matches_scene(changed, scene))
        cell['markerYawDeg'] -= 90; cell['operationalRole'] = 'NAVIGATION'
        self.assertFalse(map_matches_scene(changed, scene))


if __name__ == '__main__': unittest.main()
