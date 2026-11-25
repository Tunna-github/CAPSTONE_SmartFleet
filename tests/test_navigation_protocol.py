"""Protocol compatibility regressions; runnable on the container's Python 3.10."""
import ast
import datetime
from pathlib import Path
import time
import unittest
from unittest.mock import Mock

ROOT=Path(__file__).resolve().parents[1]


class NavigationProtocolTests(unittest.TestCase):
    def setUp(self):
        tree=ast.parse((ROOT/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py').read_text(encoding='utf-8-sig'))
        self.g={'sf_datetime':datetime,'time':time}
        names={'sf_timestamp','sf_tick_nav_gate','reached_marker'}
        exec(compile(ast.Module(body=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in names],type_ignores=[]),'navigation-protocol','exec'),self.g)

    def test_dotnet_fractional_precision_and_timezone_are_portable(self):
        for digits,microsecond in [('1',100000),('12',120000),('123',123000),('1234',123400),
            ('12345',123450),('123456',123456),('1234567',123456),('123456789',123456)]:
            result=self.g['sf_timestamp']('2026-11-25T01:02:03.'+digits+'Z')
            self.assertEqual(result,datetime.datetime(2026,11,25,1,2,3,microsecond,tzinfo=datetime.timezone.utc))
        self.assertEqual(self.g['sf_timestamp']('2026-11-25T08:02:03+07:00'),self.g['sf_timestamp']('2026-11-25T01:02:03Z'))

    def test_invalid_or_naive_timestamp_never_authorizes_motion(self):
        for raw in [None,42,'bad','2026-11-25T01:02:03','2026-99-25T01:02:03Z','2026-11-25T01:02:03.1Zgarbage']:
            self.assertIsNone(self.g['sf_timestamp'](raw))

    def test_waiting_intersection_renews_route_before_ttl_without_entering(self):
        send=Mock();stop=Mock();move=Mock(return_value=False)
        self.g.update(stop_robot=stop,sf_nav_send=send,sf_move_permission=move,
            sf_last_hold_log=time.monotonic(),sf_active_intersection=None,sf_nav_request=None,
            current_marker=4,ROBOT_CODE='R02')
        data={'phase':'NAV_WAIT_GRANT','next_index':1,'sf_route_refresh_at':0,
            'waypoints':[{'arucoId':4},{'arucoId':1,'isIntersection':True}]}
        self.g['sf_tick_nav_gate'](data)
        self.assertEqual(send.call_args.args[0],'APPROACH')
        self.assertEqual(send.call_args.args[2]['currentArUcoId'],4)
        move.assert_not_called();stop.assert_called_once()
        self.assertGreater(data['sf_route_refresh_at'],time.monotonic())

    def test_rejected_heading_keeps_intersection_and_checkpoint_until_timeout(self):
        fail=Mock();send=Mock();progress=Mock()
        self.g.update(stop_robot=Mock(),store_heading_calibration=Mock(side_effect=ValueError('bad frame')),
            cv2=Mock(error=RuntimeError),fail_mission=fail,sf_nav_send=send,send_progress=progress,
            STATE_ALIGNING='ALIGNING',STATE_IDLE='IDLE',current_marker=1,
            INTERSECTION_PILOT=True,sf_active_intersection=1)
        data={'next_index':1,'waypoints':[{'arucoId':1},{'arucoId':2}]}
        self.g['reached_marker'](data,2)
        self.assertEqual(self.g['current_marker'],1)
        send.assert_not_called();progress.assert_not_called();fail.assert_not_called()
        data['heading_confirmation_since']=time.monotonic()-6
        self.g['reached_marker'](data,2)
        fail.assert_called_once();send.assert_not_called();progress.assert_not_called()

    def test_transient_bad_heading_retries_stopped_then_reports_valid_arrival(self):
        progress=Mock()
        self.g.update(stop_robot=Mock(),store_heading_calibration=Mock(side_effect=[ValueError('bad frame'),None]),
            cv2=Mock(error=RuntimeError),fail_mission=Mock(),send_progress=progress,
            STATE_ALIGNING='ALIGNING',STATE_IDLE='IDLE',current_marker=1,
            INTERSECTION_PILOT=True,sf_active_intersection=None)
        data={'next_index':1,'waypoints':[{'arucoId':1},{'arucoId':2}]}
        self.g['reached_marker'](data,2);progress.assert_not_called()
        self.g['reached_marker'](data,2)
        progress.assert_called_once_with(data,1)
        self.assertEqual(self.g['current_marker'],2)
        self.assertNotIn('heading_confirmation_since',data)
        self.g['fail_mission'].assert_not_called()

    def test_entered_robot_keeps_reservation_without_resending_corridor_scan(self):
        send=Mock();start=Mock()
        self.g.update(stop_robot=Mock(),sf_nav_send=send,sf_move_permission=Mock(return_value=True),
            sf_start_physical_segment=start,sf_last_hold_log=0,sf_active_intersection=1,
            sf_nav_request=None,current_marker=1,ROBOT_CODE='R01')
        data={'phase':'NAV_WAIT_GRANT','next_index':1,'sf_route_refresh_at':0,
            'waypoints':[{'arucoId':1},{'arucoId':2,'isIntersection':False}]}
        self.g['sf_tick_nav_gate'](data)
        send.assert_not_called();start.assert_called_once_with(data)


if __name__=='__main__':
    unittest.main()
