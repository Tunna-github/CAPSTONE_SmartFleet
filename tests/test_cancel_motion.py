import ast
import math
from pathlib import Path
import sys
import unittest

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'Webots_Full_Mission_Controller'))
from motion_safety import measured_motion


class CancelMotionTests(unittest.TestCase):
    def controller(self):
        source=ROOT/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py'
        tree=ast.parse(source.read_text(encoding='utf8'))
        functions=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in ('handle_safety_command','reached_marker')]
        stopped=[];acks=[]
        scope=dict(mission={'missionId':1,'phase':'MOVING','encoder_origin':(0,0),'next_index':1,
                    'waypoints':[{'arucoId':64,'row':17,'col':5},{'arucoId':65,'row':17,'col':1}]},
            pending=None,current_state='SEEKING',safety_hold=False,current_marker=64,
            sf_nav_request=None,sf_nav_directive=None,safety_generation={},safety_commands={},
            visible_marker=lambda _:None,valid_int=lambda v:type(v) is int,stop_robot=lambda:stopped.append(True),
            safety_ack=lambda data,event='CONTROL_STOPPED',marker=None:acks.append((data['commandId'],event,marker)),
            store_heading_calibration=lambda _:None,ROBOT_CODE='R02',STATE_IDLE='IDLE',INTERSECTION_PILOT=True,sf_active_intersection=None)
        scope['latch_safety']=lambda _:scope.update(safety_hold=True)
        exec(compile(ast.Module(body=functions,type_ignores=[]),str(source),'exec'),scope)
        return scope,stopped,acks

    def command(self,kind='CANCEL_STOP',generation=1):
        return dict(command=kind,taskId=1,assignmentId=1,missionId=1,commandId=kind+str(generation),generation=generation)

    def test_cancel_keeps_original_leg_bounds_and_waits_for_camera_arrival(self):
        scope,stops,acks=self.controller();origin=scope['mission']['encoder_origin']
        scope['handle_safety_command'](self.command())
        self.assertEqual([],acks);self.assertFalse(scope['safety_hold']);self.assertEqual(origin,scope['mission']['encoder_origin'])
        scope['reached_marker'](scope['mission'],65)
        self.assertTrue(scope['safety_hold']);self.assertEqual([('CANCEL_STOP1','CONTROL_STOPPED',65)],acks);self.assertTrue(stops)

    def test_pause_interrupts_pending_cancel_immediately_and_fences_late_cancel(self):
        scope,stops,acks=self.controller();scope['handle_safety_command'](self.command())
        scope['handle_safety_command'](self.command('PAUSE_TASK',2))
        self.assertTrue(scope['safety_hold']);self.assertTrue(stops)
        self.assertNotIn('cancel_at_next_marker',scope['mission'])
        scope['handle_safety_command'](self.command())
        self.assertTrue(scope['safety_hold']);self.assertEqual([('PAUSE_TASK2','CONTROL_STOPPED',None)],acks)

    def test_encoder_metrics_separate_motor_speed_from_slow_rendering(self):
        m=measured_motion((0,0,0,0),(2,8,12,12))
        self.assertEqual(6,m['leftMeasuredRadPerSimSecond']);self.assertEqual(.25,m['simulationRealtimeFactor'])
        self.assertAlmostEqual(.198,m['linearMetersPerSimSecond']);self.assertAlmostEqual(.0495,m['linearMetersPerWallSecond'])
        self.assertEqual({},measured_motion(None,(0,0,0,0)))


if __name__=='__main__':unittest.main()
