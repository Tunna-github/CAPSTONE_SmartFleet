"""Marker/line demo regressions, no robot/broker connections."""
import copy
import datetime
import json
import tempfile
from pathlib import Path
import unittest
import queue
import sys
import types
from unittest.mock import MagicMock, patch
import numpy as np
import cv2
import test_physical_missions as metric
from test_physical_missions import PHYSICAL, fixture_profile, backend_map, route
from mission import MissionAgent
from protocol import validate_profile,validate_route
from line_vision import MarkerLineVision


class LineDemoTests(unittest.TestCase):
    ack=metric.PhysicalFlowTests.ack
    go=metric.PhysicalFlowTests.go
    verify_pickup=metric.PhysicalFlowTests.verify_pickup
    tearDown=metric.PhysicalFlowTests.tearDown
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.time=1000.
        self.geometry=fixture_profile()
        self.p=json.loads((PHYSICAL/'line-demo-profile.example.json').read_text())
        self.pub=[];self.motor=[]
        self.a=MissionAgent(self.p,lambda t,b:self.pub.append((t,b)),lambda v,s:self.motor.append((v,s)),
            Path(self.temp.name)/'hold.json',monotonic=lambda:self.time,server_now=lambda:self.time)
        self.a.map=backend_map(self.geometry)
        self.a.connected=True
        self.a.tick(self.sample(5))
        self.ack()

    def sample(self,ident,stationary=True,heading=None,frames=2):
        self.a.sample=dict(t=self.time,healthy=True,stationary=stationary,batteryPercent=80,
            markers={} if ident is None else {ident:dict(t=self.time,stableFrames=frames,areaPx=400)},
            lidar={'status':'CLEAR'},line=dict(t=self.time,visible=True,steer=-.08),
            localMotion=dict(x=0,y=0,yaw=0))
        return self.a.sample

    def begin(self,r=None):
        self.r=r or route(self.geometry,[5,7])
        self.a.on_command(self.r)
        self.ack();self.ack()
        self.assertEqual(self.a.state,'VERIFY')

    def test_pixel_mode_has_no_metric_configuration_or_fake_heading(self):
        validate_profile(self.p)
        self.assertNotIn('camera',self.p)
        self.assertNotIn('arrivalRadiusM',self.p)
        self.assertNotIn('encoderScaleValidated',self.p)
        body=self.pub[0][1]
        self.assertEqual(body['localizationSource'],'ARUCO_LINE_DEMO')
        self.assertIsNone(body['headingDeg'])
        self.assertNotIn('reprojectionErrorPx',body)

    def test_backend_zero_padded_labels_do_not_reject_a_configured_corridor(self):
        m=copy.deepcopy(self.a.map)
        m['links']=[[label[:2]+label[2:].zfill(2) for label in edge] for edge in m['links']]
        self.assertEqual(validate_route(self.p,m,route(self.geometry,[5,7]),5,None),'E')

    def test_explicit_checkpoint_edges_use_ids_and_preserve_directed_restrictions(self):
        m=copy.deepcopy(self.a.map)
        m['checkpointLinks']=[[5,7]]
        m['links']=[['Dairy Area','Canned Food']]
        self.assertEqual(validate_route(self.p,m,route(self.geometry,[5,7]),5,None),'E')
        m['checkpointLinks']=[]
        with self.assertRaisesRegex(ValueError,'Uncommissioned physical corridor'):
            validate_route(self.p,m,route(self.geometry,[5,7]),5,None)
        m['checkpointLinks']=[[7,5]];m['directedLinks']=True
        with self.assertRaisesRegex(ValueError,'Uncommissioned physical corridor'):
            validate_route(self.p,m,route(self.geometry,[5,7]),5,None)

    def test_line_drives_to_expected_id_and_keeps_program_ready_for_a_second_task(self):
        self.begin();self.verify_pickup();self.drive_segment()
        self.assertEqual(self.motor[-1],(.1,-.08))
        self.a.tick(self.sample(8))  # unrelated marker cannot advance the route.
        self.assertIsNone(self.a.pending)
        self.assertEqual(self.a.index,1)
        self.a.tick(self.sample(7,stationary=False,frames=1))
        self.assertEqual(self.motor[-1],(0,0))
        self.assertIsNone(self.a.pending)
        self.a.tick(self.sample(7))
        self.assertEqual(self.a.pending['body']['arucoId'],7)
        self.ack();self.a.tick(self.a.sample)
        self.ack('WAIT_DELIVERY_VERIFICATION')
        self.assertEqual(self.a.state,'VERIFY')
        self.time+=2.1;self.a.tick(self.sample(7));self.ack('COMPLETE');self.ack()
        self.assertEqual(self.a.state,'IDLE')
        self.assertTrue(self.a.connected)
        self.time+=2.1;self.a.tick(self.sample(7));self.ack()
        self.begin(route(self.geometry,[7,4],task=2))
        self.verify_pickup();self.drive_segment(seq=2)
        self.assertEqual(self.a.route['taskId'],2)
        self.assertGreater(self.motor[-1][0],0)

    def test_delayed_verification_ack_keeps_same_task_and_request_stopped(self):
        self.begin();self.a.tick(self.sample(5))
        event=self.a.pending['body']['eventId']
        for _ in range(4):
            self.time+=11;self.a.tick(self.sample(5))
            self.assertEqual(self.a.state,'VERIFY')
            self.assertEqual(self.a.route['taskId'],self.r['taskId'])
            self.assertEqual(self.a.pending['body']['eventId'],event)
            self.assertEqual(self.motor[-1],(0,0))
        self.assertIsNotNone(self.a.communication_warning)
        self.assertFalse(any(b.get('event')=='MISSION_FAILED' for _,b in self.pub))
        self.ack('CONTINUE')
        self.assertIsNone(self.a.communication_warning)
        self.assertEqual(self.a.pending['kind'],'SCAN')
        self.assertEqual(self.motor[-1],(0,0))  # Still needs a fresh navigation grant.

    def test_verification_retry_and_late_continue_cannot_use_a_lost_marker(self):
        self.begin();self.a.tick(self.sample(5))
        before=len(self.pub);self.time+=11;self.a.tick(self.sample(None))
        self.assertEqual(len(self.pub),before)
        self.ack('CONTINUE')
        self.assertEqual(self.a.state,'VERIFY');self.assertIsNone(self.a.pending)
        self.assertEqual(self.motor[-1],(0,0))
        self.a.tick(self.sample(5))
        self.assertEqual(self.a.pending['kind'],'VERIFICATION_STATUS')
        self.ack('CONTINUE');self.assertEqual(self.a.pending['kind'],'SCAN')

    def test_missing_mission_start_ack_still_fails_safely(self):
        self.a.on_command(route(self.geometry,[5,7]))
        self.time+=11;self.a.tick(self.sample(5))
        self.assertEqual(self.a.state,'FAILED');self.assertFalse(self.a.ready)
        self.assertEqual(self.motor[-1],(0,0))

    def test_previous_attempt_control_cannot_interrupt_the_current_task(self):
        self.begin();self.verify_pickup();self.drive_segment()
        before=len(self.motor)
        self.a.on_command(dict(command='FORCE_CANCEL',commandId='old-attempt',
            taskId=self.r['taskId'],assignmentId=self.r['assignmentId']-1,missionId=self.r['missionId']-1))
        self.assertEqual(len(self.motor),before)
        self.assertEqual(self.a.state,'MOVING')
        self.assertGreater(self.motor[-1][0],0)

    def test_runtime_subscribes_to_the_backend_navigation_ack_contract(self):
        from runtime import PhysicalRuntime
        client=MagicMock()
        paho=types.ModuleType('paho');paho.__path__=[]
        mqtt=types.ModuleType('paho.mqtt');mqtt.__path__=[]
        mqtt_client=types.ModuleType('paho.mqtt.client');mqtt_client.Client=lambda **kwargs:client
        paho.mqtt=mqtt;mqtt.client=mqtt_client
        runtime=PhysicalRuntime.__new__(PhysicalRuntime)
        runtime.p=self.p;runtime.io=MagicMock();runtime.agent=self.a
        runtime.messages=queue.Queue();runtime.loop=lambda:None
        with patch.dict(sys.modules,{'paho':paho,'paho.mqtt':mqtt,'paho.mqtt.client':mqtt_client}):
            runtime.start()
            client.on_connect(client,None,{},0)
            topics=[call.args[0] for call in client.subscribe.call_args_list]
            self.assertIn(self.a.topic('navigation-ack'),topics)
            self.assertNotIn(self.a.topic('nav-ack'),topics)
            runtime.close()

    def test_navigation_before_clock_handshake_holds_without_crashing_or_accepting_a_grant(self):
        from runtime import PhysicalRuntime
        runtime=PhysicalRuntime.__new__(PhysicalRuntime)
        runtime.agent=self.a;runtime.io=MagicMock();runtime.clock_anchor=None
        directive=dict(robotCode=self.a.code,epoch='startup',sequence=1,mode='GO',maxLinearSpeed=.1,
            expiresAtUtc=datetime.datetime.fromtimestamp(self.time+3,datetime.timezone.utc).isoformat())
        runtime.receive('nav-control',directive)
        runtime.io.drive.assert_called_with(0,0)
        self.assertIsNone(self.a.directive);self.assertEqual(self.a.nav_sequence,-1)
        runtime.clock_anchor=(self.time,self.time)
        runtime.receive('nav-control',directive)
        self.assertIsNotNone(self.a.directive)

    def drive_segment(self,intersection=None,seq=1):
        self.ack()
        self.go(intersection,intersection is not None,seq=seq)
        self.a.tick(self.a.sample)
        if self.a.pending and self.a.pending['kind']=='ENTER':
            self.ack();self.a.tick(self.a.sample)
        self.a.tick(self.a.sample)
        self.assertGreater(self.motor[-1][0],0)

    def test_line_loss_or_stale_frame_stops_without_fake_arrival(self):
        self.begin();self.verify_pickup();self.drive_segment()
        sample=self.sample(None);sample['line']['visible']=False
        self.a.tick(sample)
        self.assertEqual(self.motor[-1],(0,0));self.assertIsNone(self.a.pending)
        sample=self.sample(None);sample['line']['t']=self.time-1
        self.a.tick(sample)
        self.assertEqual(self.motor[-1],(0,0))

    def test_idle_camera_loss_revokes_ready_instead_of_accepting_a_stale_start(self):
        self.assertTrue(self.a.ready)
        self.a.tick(self.sample(None))
        self.assertFalse(self.a.ready)
        self.assertEqual(self.pub[-1][1]['event'],'LOCALIZATION_LOST')
        with self.assertRaises(ValueError):self.a.on_command(route(self.geometry,[5,7]))

    def test_operator_near_stopped_car_does_not_block_verification_presence(self):
        self.begin()
        sample=self.sample(5);sample['lidar']['status']='CRITICAL'
        self.a.tick(sample)
        self.assertEqual(self.a.pending['kind'],'VERIFICATION_STATUS')
        self.assertEqual(self.motor[-1],(0,0))
        self.ack('CONTINUE');self.ack();self.go()
        self.a.tick(sample)
        self.assertEqual(self.motor[-1],(0,0)) # obstacle still prevents departure.

    def test_center_marker_never_releases_intersection_as_exit(self):
        self.a.current=0;self.sample(0)
        self.begin(route(self.geometry,[0,6,2]));self.verify_pickup();self.drive_segment(intersection=6)
        self.a.tick(self.sample(6));self.ack()
        self.assertEqual(self.a.protected,6)
        self.a.tick(self.a.sample);self.a.tick(self.a.sample)
        sample=self.sample(2);sample['markers'][6]=dict(t=self.time,stableFrames=2,areaPx=400)
        self.a.tick(sample)
        self.assertIsNone(self.a.pending);self.assertEqual(self.a.protected,6)
        self.a.tick(self.sample(2))
        self.assertEqual(self.a.pending['kind'],'EXIT');self.assertEqual(self.a.protected,6)
        self.ack();self.assertIsNone(self.a.protected)


class LinePixelsTests(unittest.TestCase):
    def setUp(self):
        self.p=json.loads((PHYSICAL/'line-demo-profile.example.json').read_text())

    def test_steering_sign_center_and_line_loss(self):
        for x,sign in [(320,0),(160,1),(480,-1)]:
            gray=np.full((480,640),255,np.uint8)
            cv2.line(gray,(x,110),(x,430),0,12)
            reader=MarkerLineVision(self.p,cv2,np);reader.vision(gray,[],[],1)
            self.assertTrue(reader.line['visible'])
            if sign:self.assertGreater(reader.line['steer']*sign,0)
            else:self.assertLess(abs(reader.line['steer']),.01)
        reader.vision(np.full((480,640),255,np.uint8),[],[],1.05)
        self.assertFalse(reader.line['visible'])

    def test_marker_polygon_is_not_mistaken_for_tape_and_streak_resets_on_missing_frame(self):
        gray=np.full((480,640),255,np.uint8)
        cv2.rectangle(gray,(255,200),(385,360),0,-1)
        corners=np.array([[[255,200],[385,200],[385,360],[255,360]]],np.float32)
        reader=MarkerLineVision(self.p,cv2,np)
        reader.vision(gray,[corners],[7],1)
        self.assertFalse(reader.line['visible'])
        reader.vision(gray,[corners],[7],1.05);self.assertEqual(reader.markers[7]['stableFrames'],2)
        reader.vision(gray,[],[],1.10)
        reader.vision(gray,[corners],[7],1.15);self.assertEqual(reader.markers[7]['stableFrames'],1)


if __name__=='__main__':unittest.main()
