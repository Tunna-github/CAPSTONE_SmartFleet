"""Exercise the actual controller's verification gates without Webots hardware."""
import ast
from pathlib import Path
import time
import unittest
from unittest.mock import Mock

class VerificationGateTests(unittest.TestCase):
    def setUp(self):
        path = Path(__file__).resolve().parents[1] / 'Webots_Full_Mission_Controller/my_controller_testing_fleet.py'
        tree = ast.parse(path.read_text(encoding='utf-8-sig'))
        code = ast.Module(body=[n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name in ('tick_mission','poll_verification')], type_ignores=[])
        self.data = dict(missionId=1,taskId=1,assignmentId=1,phase='WAIT_WAYPOINT_ACK',next_index=0,
                         pickupWaypointIndex=1,deliveryArUcoId=2,mapRevision=5,
                         waypoints=[dict(arucoId=i) for i in range(3)])
        self.g = dict(time=time,mission=self.data,pending=None,safety_hold=False,connected=True,
                      INTERSECTION_PILOT=False,current_marker=1,current_state='IDLE',STATE_IDLE='IDLE',
                      stop_robot=Mock(),tick_pending=Mock(return_value=None),begin_segment=Mock(),
                      send_status=Mock(),fail_mission=Mock(),send_reliable=Mock(),visible_marker=Mock(return_value=object()),
                      QR_PROGRESS_TOPIC='progress',QR_PROGRESS_ACK_TOPIC='ack')
        self.g.update(heading_fusion=Mock(),get_yaw_deg=lambda:0)
        self.g['heading_fusion'].heading.return_value=0.0
        exec(compile(code,'verification-gates','exec'),self.g)

    def outcome(self,kind,index,action=''):
        self.g['tick_pending'].return_value=(True,action,kind,index)
        self.g['tick_mission']()

    def test_pickup_arrival_stops_until_verified(self):
        self.outcome('WAYPOINT_REACHED',1)
        self.assertEqual('WAIT_VERIFICATION',self.data['phase'])
        self.g['begin_segment'].assert_not_called()
        self.g['send_status'].assert_not_called()

    def test_delivery_arrival_does_not_complete_without_verification(self):
        self.g['current_marker']=2
        self.outcome('WAYPOINT_REACHED',2)
        self.assertEqual('WAIT_VERIFICATION',self.data['phase'])
        self.g['send_status'].assert_not_called()

    def test_wait_instruction_never_moves(self):
        self.outcome('VERIFICATION_STATUS',1,'WAIT_PICKUP_VERIFICATION')
        self.g['begin_segment'].assert_not_called()
        self.g['stop_robot'].assert_called()

    def test_verified_pickup_can_continue(self):
        self.outcome('VERIFICATION_STATUS',1,'CONTINUE')
        self.g['begin_segment'].assert_called_once_with(self.data)

    def test_verified_delivery_reports_completion(self):
        self.g['current_marker']=2
        self.outcome('VERIFICATION_STATUS',2,'COMPLETE')
        self.g['send_status'].assert_called_once_with('MISSION_COMPLETED',self.data)

    def test_offline_robot_does_not_move_or_poll(self):
        self.g['connected']=False
        self.g['tick_mission']()
        self.g['begin_segment'].assert_not_called()
        self.g['tick_pending'].assert_not_called()

    def test_missing_camera_pose_does_not_claim_station_presence(self):
        self.data['next_index']=2
        self.g['visible_marker'].return_value=None
        self.g['poll_verification'](self.data)
        self.g['send_reliable'].assert_not_called()
        self.g['stop_robot'].assert_called()

    def test_station_poll_correlates_camera_checkpoint_and_frozen_map(self):
        self.data['next_index']=2
        self.g['poll_verification'](self.data)
        body=self.g['send_reliable'].call_args.args[2]
        self.assertEqual(1,body['arucoId'])
        self.assertEqual(1,body['waypointIndex'])
        self.assertEqual(5,body['mapRevision'])
        self.assertTrue(body['robotStopped'])
        self.assertEqual(0.0,body['headingDeg'])

if __name__=='__main__': unittest.main()
