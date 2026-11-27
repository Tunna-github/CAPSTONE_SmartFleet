import sys
import unittest
import ast
import json
import os
import tempfile
import uuid
from types import SimpleNamespace
from unittest.mock import Mock
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'Webots_Full_Mission_Controller'))
from task_resume import PausedMission


class TaskResumeTests(unittest.TestCase):
    def test_actual_controller_waits_for_backend_ack_before_releasing_pause(self):
        names={'latch_safety','safety_ack','capture_pause_runtime','request_task_resume','tick_task_resume','handle_safety_command'}
        tree=ast.parse((ROOT/'Webots_Full_Mission_Controller/my_controller_testing_fleet.py').read_text(encoding='utf-8-sig'))
        functions=ast.Module(body=[n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name in names],type_ignores=[])
        with tempfile.TemporaryDirectory() as folder:
            mission=dict(taskId=1,assignmentId=2,missionId=3,mapRevision=4,next_index=3,phase='MOVING',encoder_origin=(1,2),segment_started_at=90)
            sim=Mock();sim.getTime.return_value=100
            g=dict(PausedMission=PausedMission,uuid=uuid,json=json,os=os,time=SimpleNamespace(monotonic=lambda:200),robot=sim,
                mission=mission,pending=None,pause_runtime=None,resume_transition=None,safety_hold=False,
                safety_generation={},safety_commands={},SAFETY_FILE=folder+'/hold',controller_session_id=str(uuid.uuid4()),
                sf_nav_epoch='epoch',sf_nav_request=None,sf_nav_directive=None,sf_reconnected_with_mission=False,
                left_encoder=SimpleNamespace(getValue=lambda:4),right_encoder=SimpleNamespace(getValue=lambda:5),get_yaw_deg=lambda:0,
                connected=True,localization_valid=True,sf_local_obstacle='CLEAR',sf_wheel_scale=lambda:1,
                stop_robot=Mock(),publish=Mock(),STATUS_TOPIC='status',STATUS_ACK_TOPIC='ack',
                valid_int=lambda v:type(v) is int,current_marker=0,current_state='MOVING',visible_marker=lambda _:object(),
                state_start_time=90,mission_camera_lost_since=None,ACK_TIMEOUT_SECONDS=30,ACK_RETRY_SECONDS=1)
            g['send_reliable']=lambda *args,**kwargs:g.update(pending={'kind':args[0]})
            exec(compile(functions,'controller-resume','exec'),g)
            pause=dict(command='PAUSE_TASK',commandId='pause',generation=1,taskId=1,assignmentId=2,missionId=3)
            g['handle_safety_command'](pause)
            context=g['publish'].call_args.args[1]['resumeContext']
            g['handle_safety_command'](dict(pause,command='RESUME_TASK',commandId='resume',generation=2,route=context))
            self.assertTrue(g['safety_hold']);self.assertTrue(Path(g['SAFETY_FILE']).exists())
            g['tick_pending']=lambda:None;g['tick_task_resume']();self.assertTrue(g['safety_hold'])
            sim.getTime.return_value=160;g['time'].monotonic=lambda:260
            g['tick_pending']=lambda:(True,'Applied','CONTROL_RESUMED',None)
            g['tick_task_resume']()
            self.assertFalse(g['safety_hold']);self.assertFalse(Path(g['SAFETY_FILE']).exists())
            self.assertIs(mission,g['mission']);self.assertEqual(3,mission['next_index']);self.assertEqual((1,2),mission['encoder_origin'])
            self.assertEqual(150,mission['segment_started_at']);self.assertEqual(150,g['state_start_time'])

    def fixture(self):
        mission=dict(taskId=1,assignmentId=2,missionId=3,mapRevision=4,phase='MOVING',next_index=8,
            encoder_origin=(10,20),segment_started_at=90,pickupVerified=True)
        pending=dict(kind='WAYPOINT_REACHED',body={'eventId':'old'},accepted=None,eventId='old',index=7)
        saved=PausedMission(mission,pending,100,200,(12,22),0,'session','epoch')
        saved.bind({'commandId':'pause'})
        command=dict(taskId=1,assignmentId=2,missionId=3,route=dict(saved.context))
        return saved,mission,command

    def test_restore_excludes_pause_time_without_restarting_leg_or_losing_load(self):
        saved,m,c=self.fixture();request=dict(deadline=210,retry_at=201)
        self.assertTrue(saved.matches(c,m,(12,22),0,'epoch'))
        pending,delta=saved.restore(160,260,request,30,1)
        self.assertEqual(60,delta);self.assertEqual(150,m['segment_started_at'])
        self.assertEqual((10,20),m['encoder_origin']);self.assertEqual(8,m['next_index']);self.assertTrue(m['pickupVerified'])
        self.assertEqual(7,pending['index']);self.assertNotEqual('old',pending['eventId']);self.assertEqual(290,request['deadline'])

    def test_restart_changed_heading_or_manual_movement_cannot_resume(self):
        saved,m,c=self.fixture()
        self.assertFalse(saved.matches(c,dict(m),(12,22),0,'epoch'))
        self.assertFalse(saved.matches(c,m,(13,22),0,'epoch'))
        self.assertFalse(saved.matches(c,m,(12,22),45,'epoch'))
        self.assertFalse(saved.matches(c,m,(12,22),0,'new-epoch'))

    def test_new_pause_fences_old_resume_token(self):
        saved,m,c=self.fixture();saved.bind({'commandId':'new-pause'})
        self.assertFalse(saved.matches(c,m,(12,22),0,'epoch'))

if __name__=='__main__':unittest.main()
