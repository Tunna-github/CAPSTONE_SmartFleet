import json
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'Webots_Full_Mission_Controller'))
from charging_protocol import ChargingJournal,validate_route
from motion_safety import front_lidar_report


class ChargingProtocolTests(unittest.TestCase):
    def test_camera_at_another_marker_cannot_renew_dock_presence(self):
        import ast
        from types import SimpleNamespace
        source=Path(__file__).resolve().parents[1]/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py'
        function=next(n for n in ast.parse(source.read_text(encoding='utf-8')).body if isinstance(n,ast.FunctionDef) and n.name=='tick_charging')
        faults=[]
        scope=dict(connected=True,time=SimpleNamespace(monotonic=lambda:100.),charging_last_report=0.,charging_fault_report=None,
            charging_dock={'waypoints':[{'arucoId':67}]},safety_hold=False,mission=None,localization_valid=True,ready_acknowledged=True,
            current_marker=42,charging_fault=lambda data,reason:faults.append(reason))
        exec(compile(ast.Module(body=[function],type_ignores=[]),str(source),'exec'),scope)
        scope['tick_charging']();self.assertEqual(1,len(faults))
    def test_dock_heartbeat_waits_during_stationary_camera_relocalization(self):
        import ast
        from types import SimpleNamespace
        source=Path(__file__).resolve().parents[1]/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py'
        function=next(n for n in ast.parse(source.read_text(encoding='utf-8')).body if isinstance(n,ast.FunctionDef) and n.name=='tick_charging')
        def unexpected(_):raise AssertionError('Cannot query a marker before localization has a current marker')
        scope=dict(connected=True,time=SimpleNamespace(monotonic=lambda:100.),charging_last_report=0.,charging_fault_report=None,
            charging_dock={},safety_hold=False,mission=None,localization_valid=False,current_marker=None,visible_marker=unexpected)
        exec(compile(ast.Module(body=[function],type_ignores=[]),str(source),'exec'),scope)
        scope['tick_charging']()
    def test_presence_uses_wall_clock_even_when_simulated_time_does_not_advance(self):
        import ast
        from types import SimpleNamespace
        source=Path(__file__).resolve().parents[1]/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py'
        tree=ast.parse(source.read_text(encoding='utf-8'))
        function=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='tick_telemetry')
        clock=SimpleNamespace(now=100.);sent=[]
        scope=dict(connected=True,last_telemetry_at=-100.,time=SimpleNamespace(monotonic=lambda:clock.now),
            publish=lambda topic,body,**kwargs:sent.append(body),TELEMETRY_TOPIC='test',SIMULATED_BATTERY_PERCENT=85,
            SIMULATED_BATTERY_VOLTAGE=12,get_yaw_deg=lambda:0,localization_valid=True,localization_reason='test')
        exec(compile(ast.Module(body=[function],type_ignores=[]),str(source),'exec'),scope)
        for now in [100.,101.,102.1]:clock.now=now;scope['tick_telemetry']()
        self.assertEqual(2,len(sent))
    def test_laser_packets_match_backend_thresholds_and_clear_schema(self):
        import math
        for distance in [float('inf'),3.4,.501,.50,.251,.25,.24]:
            packet=front_lidar_report([distance]*360,2*math.pi)
            if distance>.5:self.assertEqual({'status':'CLEAR','distanceCm':None},packet)
            elif distance>.25:self.assertEqual('DETECTED',packet['status']);self.assertGreater(packet['distanceCm'],25)
            else:self.assertEqual('CRITICAL',packet['status']);self.assertLessEqual(packet['distanceCm'],25)
        self.assertEqual('NO_SCAN_DATA',front_lidar_report([float('nan')]*360,2*math.pi)['status'])
    def command(self):
        return dict(chargingJobId=str(uuid.uuid4()),generation=1,route=dict(mapRevision=8,startArUcoId=0,homeArUcoId=67,
            waypoints=[dict(arucoId=0,row=1,col=1,isIntersection=False),dict(arucoId=67,row=2,col=1,isIntersection=False,travelDirection='S')]))

    def test_journal_rejects_delayed_commands_after_restart_and_new_mission(self):
        with tempfile.TemporaryDirectory() as d:
            path=str(Path(d)/'journal.json');command=self.command();journal=ChargingJournal(path)
            self.assertTrue(journal.accepts_new(command));journal.save(command,'ACTIVE')
            restored=ChargingJournal(path);self.assertEqual('ACTIVE',restored.saved['state']);self.assertFalse(restored.accepts_new(command))
            restored.save(command,'COMPLETED');late={**command,'chargingJobId':str(uuid.uuid4())}
            self.assertFalse(ChargingJournal(path).accepts_new(late))
            late['generation']=2;self.assertTrue(restored.accepts_new(late))

    def test_corrupt_journal_holds_robot(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d)/'j.json';p.write_text('broken');j=ChargingJournal(str(p))
            self.assertEqual('FAILED',j.saved['state']);self.assertFalse(j.accepts_new(self.command()))

    def test_route_requires_current_camera_position_and_map(self):
        c=self.command();self.assertEqual(2,len(validate_route(c,0,8)))
        for marker,revision in [(4,8),(0,7)]:
            with self.assertRaises(ValueError):validate_route(c,marker,revision)

    def test_wrong_heading_or_diagonal_is_not_executable(self):
        for changes in [dict(travelDirection='N'),dict(col=2)]:
            c=self.command();c['route']['waypoints'][-1].update(changes)
            with self.assertRaises(ValueError):validate_route(c,0,8)

    def test_stationary_recovery_must_still_match_endpoint(self):
        c=self.command();c['route'].update(homeArUcoId=0,waypoints=c['route']['waypoints'][:1])
        self.assertEqual(1,len(validate_route(c,0,8)))
        c['route']['homeArUcoId']=67
        with self.assertRaises(ValueError):validate_route(c,0,8)


if __name__=='__main__':unittest.main()
