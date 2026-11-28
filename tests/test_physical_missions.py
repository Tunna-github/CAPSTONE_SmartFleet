"""Offline, injected-sensor protocol tests. These do not drive or connect to a robot."""
import copy
import datetime
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[1]
PHYSICAL=ROOT/'Robot code'/'physical'
sys.path.insert(0,str(PHYSICAL))
from protocol import validate_profile,validate_map,validate_route,utc
from mission import MissionAgent


def fixture_profile():
    p=json.loads((PHYSICAL/'physical-profile.example.json').read_text())
    # SYNTHETIC test fixture, never written into the installation example.
    p.update(commissioned=True,encoderScaleValidated=True,arrivalRadiusM=.2,intersectionClearanceM=.5,
        maxVisionGapSeconds=10,maxOdometryGapM=1.5)
    p['camera']['validated']=True
    for m in p['markers']:
        m.update(markerSizeM=.18,markerYawDeg=0)
    return p


def backend_map(p):
    return dict(frame='MAP_N_W_UP',markerDictionary='DICT_4X4_50',mapRevision=9,
        metersPerGridCell=p['metersPerGridCell'],markers=copy.deepcopy(p['markers']),links=p['links'],directedLinks=False)


def route(p,ids,pi=0,task=1):
    from protocol import direction
    by_id={m['arucoId']:m for m in p['markers']}
    points=[]
    for i,ident in enumerate(ids):
        w={k:by_id[ident][k] for k in ('arucoId','row','col','isIntersection')}
        w.update(travelDirection=None if i==0 else direction(points[-1],w),
                 stage='PICKUP' if i==pi else 'DELIVERY' if i==len(ids)-1 else 'TO_DELIVERY')
        points.append(w)
    return dict(command='EXECUTE_TASK',pattern='WAYPOINT_ROUTE',verificationRequired=True,
        taskId=task,assignmentId=task,missionId=task,mapRevision=9,startArUcoId=ids[0],pickupArUcoId=ids[pi],
        deliveryArUcoId=ids[-1],pickupWaypointIndex=pi,metersPerGridCell=p['metersPerGridCell'],
        routeToPickup=ids[:pi+1],routeToDelivery=ids[pi:],waypoints=points)


class PhysicalFlowTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory()
        self.time=1000.
        self.p=fixture_profile()
        self.pub=[]
        self.motor=[]
        self.a=MissionAgent(self.p,lambda topic,body:self.pub.append((topic,body)),lambda v,s:self.motor.append((v,s)),
            Path(self.temp.name)/'hold.json',monotonic=lambda:self.time,server_now=lambda:self.time)
        self.a.map=backend_map(self.p)
        self.a.connected=True
        self.sample(5)
        self.a.tick(self.a.sample)
        self.ack()
        self.assertTrue(self.a.ready)

    def tearDown(self):
        self.temp.cleanup()

    def sample(self,ident,stationary=True,heading=-90):
        marker=next(m for m in self.p['markers'] if m['arucoId']==ident)
        self.a.sample=dict(t=self.time,healthy=True,poseValid=True,stationary=stationary,
            headingDeg=heading,x=-marker['row']*.3,y=-marker['col']*.3,batteryPercent=80,
            lidar={'status':'CLEAR'},markers={ident:dict(t=self.time,stableFrames=6,distanceM=.05,reprojectionErrorPx=.2)})
        return self.a.sample

    def ack(self,action=None,accepted=True):
        p=self.a.pending
        self.assertIsNotNone(p)
        body=p['body']
        response=dict(eventId=body['eventId'],accepted=accepted,assignmentId=body.get('assignmentId'),
            missionId=body.get('missionId'),robotCode=self.a.code,kind=p['kind'])
        if action:response['action']=action
        suffix={'status':'status-ack','qr-progress':'qr-progress-ack','scan':'navigation-ack','navigation':'navigation-ack'}[p['suffix']]
        self.a.ack(suffix,response)

    def go(self,intersection=None,granted=False,seq=1,mode='GO'):
        self.a.nav(dict(robotCode='R01',epoch='epoch-1',sequence=seq,mode=mode,maxLinearSpeed=.1,
            intersectionArUcoId=intersection,intersectionGranted=granted,
            expiresAtUtc=datetime.datetime.fromtimestamp(self.time+3,datetime.timezone.utc).isoformat()))

    def begin(self,r=None):
        self.r=r or route(self.p,[5,7,4])
        self.a.on_command(self.r)
        self.assertEqual(self.a.pending['kind'],'MISSION_STARTED')
        self.ack()
        self.assertEqual(self.a.pending['kind'],'WAYPOINT_REACHED')
        self.ack()
        self.assertEqual(self.a.state,'VERIFY')

    def verify_pickup(self):
        self.a.tick(self.a.sample)
        self.assertEqual(self.a.pending['kind'],'VERIFICATION_STATUS')
        self.ack('CONTINUE')

    def drive_segment(self,intersection=None):
        self.ack()  # SCAN/APPROACH applied.
        self.go(intersection,intersection is not None)
        self.a.tick(self.a.sample)
        if self.a.pending and self.a.pending['kind']=='ENTER':
            self.ack()
            self.a.tick(self.a.sample)
        self.a.tick(self.a.sample)
        self.assertGreater(self.motor[-1][0],0)

    def test_task_selected_straight_route_waits_for_both_transfer_confirmations(self):
        self.begin()
        # READY/started/progress ACKs alone never authorize departure from pickup.
        self.assertFalse(any(v for v,_ in self.motor))
        self.verify_pickup()
        self.drive_segment()
        self.a.tick(self.sample(7,stationary=False))
        self.assertIsNone(self.a.pending)  # physical stop not yet confirmed.
        self.a.tick(self.sample(7))
        self.assertEqual(self.a.pending['body']['arucoId'],7)
        self.ack()
        self.drive_segment()
        self.a.tick(self.sample(4))
        self.ack()
        self.a.tick(self.a.sample)
        self.ack('WAIT_DELIVERY_VERIFICATION')
        self.assertNotIn('MISSION_COMPLETED',[body.get('event') for _,body in self.pub])
        self.time+=2.1
        self.a.tick(self.sample(4))
        self.ack('COMPLETE')
        self.assertEqual(self.a.pending['kind'],'MISSION_COMPLETED')
        self.ack()
        self.assertEqual(self.a.state,'IDLE')
        self.assertFalse(self.a.path.exists())

    def test_intersection_needs_matching_grant_enter_ack_and_camera_exit(self):
        self.a.current=0
        self.sample(0)
        self.begin(route(self.p,[0,6,2]))
        self.verify_pickup()
        self.assertEqual(self.a.pending['kind'],'APPROACH')
        self.ack()
        self.go(6,False,mode='STOP')
        self.a.tick(self.a.sample)
        self.assertEqual(self.motor[-1],(0,0))
        self.go(6,True,seq=2)
        self.a.tick(self.a.sample)
        self.assertEqual(self.a.pending['kind'],'ENTER')
        self.assertEqual(self.motor[-1],(0,0))
        self.ack()
        self.a.tick(self.a.sample)
        self.a.tick(self.a.sample)
        self.assertGreater(self.motor[-1][0],0)
        self.a.tick(self.sample(6))
        self.ack()
        self.assertEqual(self.a.protected,6)
        self.assertIsNone(self.a.pending) # no new SCAN releases occupied intersection.
        self.a.tick(self.a.sample)
        self.a.tick(self.a.sample)
        self.a.tick(self.sample(2))
        self.assertEqual(self.a.pending['kind'],'EXIT')
        self.assertEqual(self.a.protected,6) # held until actual backend EXIT ACK.
        self.ack()
        self.assertIsNone(self.a.protected)

    def test_expired_wrong_intersection_and_old_sequence_do_not_move(self):
        self.begin()
        self.verify_pickup()
        self.ack()
        self.go(6,True)
        self.a.tick(self.a.sample)
        self.assertEqual(self.motor[-1],(0,0))
        self.go(seq=2)
        self.time+=3.1
        self.a.tick(self.sample(5))
        self.assertEqual(self.motor[-1],(0,0))
        self.go(seq=1)
        self.assertEqual(self.a.nav_sequence,2)

    def test_qos_duplicate_does_not_restart_progress_or_replace_route(self):
        self.begin()
        index=self.a.index
        self.a.on_command(self.r)
        self.assertEqual(self.a.index,index)
        with self.assertRaises(ValueError):self.a.on_command(route(self.p,[5,7],task=2))
        self.assertEqual(self.a.route['taskId'],1)
        self.assertEqual(self.a.state,'FAILED')
        self.a.tick(self.a.sample)
        self.assertEqual(self.motor[-1],(0,0))

    def test_pause_while_awaiting_progress_cannot_be_overridden_by_late_ack(self):
        self.a.on_command(route(self.p,[5,7]))
        self.ack()
        old=dict(self.a.pending['body'])
        self.sample(5,stationary=False)
        self.a.control(dict(command='PAUSE_TASK',commandId='pause-moving',taskId=1,assignmentId=1,missionId=1))
        self.assertEqual(self.a.state,'STOPPING_CONTROL')
        self.a.ack('qr-progress-ack',dict(eventId=old['eventId'],accepted=True,missionId=1))
        self.assertEqual(self.a.state,'STOPPING_CONTROL')
        self.a.tick(self.sample(5))
        self.assertEqual(self.a.state,'PAUSED')
        self.assertEqual(self.a.pending['kind'],'CONTROL_STOPPED')

    def test_retry_uses_same_event_and_wrong_ack_never_advances(self):
        self.begin()
        self.a.tick(self.a.sample)
        event=self.a.pending['body']['eventId']
        self.a.ack('qr-progress-ack',dict(eventId='wrong',accepted=True))
        self.assertEqual(self.a.pending['body']['eventId'],event)
        self.time+=2.1
        self.a.tick(self.sample(5))
        self.assertEqual(self.pub[-1][1]['eventId'],event)

    def test_pause_resume_keeps_same_route_load_and_lock_context(self):
        self.begin()
        self.verify_pickup()
        self.drive_segment()
        self.sample(5)
        pause=dict(command='PAUSE_TASK',commandId='pause',taskId=1,assignmentId=1,missionId=1)
        self.a.control(pause)
        self.assertEqual(self.a.state,'PAUSED')
        saved_route=self.a.route
        context=copy.deepcopy(self.a.paused['context'])
        self.ack()
        self.a.control(dict(pause,command='RESUME_TASK',commandId='resume',route=context))
        self.assertEqual(self.a.state,'RESUME_ACK')
        self.assertEqual(self.motor[-1],(0,0))
        self.ack()
        self.assertIs(self.a.route,saved_route)
        self.assertIsNone(self.a.directive)
        self.a.tick(self.a.sample)
        self.assertEqual(self.motor[-1],(0,0))
        self.go(seq=2)
        self.a.tick(self.a.sample)
        self.assertGreater(self.motor[-1][0],0)

    def test_restart_and_cancel_never_invent_return_home_or_resume(self):
        self.begin()
        restarted=MissionAgent(self.p,lambda *_:None,lambda v,s:self.motor.append((v,s)),self.a.path)
        self.assertEqual(restarted.state,'RECOVERY_HOLD')
        self.assertIsNotNone(restarted.route) # identity only, no execution recovery.
        restarted.on_command(self.r) # redelivery may be ignored, never reconstructs motion.
        self.assertEqual(restarted.state,'RECOVERY_HOLD')
        self.assertEqual(self.motor[-1],(0,0))
        self.a.pending=None
        self.a.control(dict(command='CANCEL_STOP',commandId='cancel',taskId=1,assignmentId=1,missionId=1))
        self.assertEqual(self.a.state,'RECOVERY_HOLD')
        self.assertEqual(self.a.pending['kind'],'CONTROL_STOPPED')

    def test_missing_lidar_or_vision_stops_without_waypoint_or_release(self):
        self.begin()
        self.verify_pickup()
        self.drive_segment()
        sample=self.sample(5)
        sample['lidar']['status']='NO_SCAN_DATA'
        self.a.tick(sample)
        self.assertEqual(self.motor[-1],(0,0))
        self.assertIsNone(self.a.pending)
        sample['lidar']['status']='CLEAR'
        sample['poseValid']=False
        self.a.tick(sample)
        self.assertEqual(self.motor[-1],(0,0))


class PhysicalValidationTests(unittest.TestCase):
    def test_unmeasured_example_cannot_start_hardware(self):
        p=json.loads((PHYSICAL/'physical-profile.example.json').read_text())
        with self.assertRaisesRegex(ValueError,'commissioning'):validate_profile(p)
        from runtime import PhysicalRuntime
        with self.assertRaises(ValueError):PhysicalRuntime(PHYSICAL/'physical-profile.example.json')

    def test_scene_mismatch_duplicate_marker_and_uncommissioned_turn_rejected(self):
        p=fixture_profile()
        m=backend_map(p)
        self.assertEqual(validate_route(p,m,route(p,[5,7,4]),5,-90),'E')
        with self.assertRaises(ValueError):validate_route(p,m,route(p,[5,7,6]),5,-90)
        m['markers'][0]['markerSizeM']=.04
        with self.assertRaises(ValueError):validate_map(p,m)
        p['markers'].append(dict(p['markers'][0]))
        with self.assertRaises(ValueError):validate_profile(p)

    def test_wrong_heading_and_station_mismatch_rejected(self):
        p=fixture_profile()
        with self.assertRaises(ValueError):validate_route(p,backend_map(p),route(p,[5,7]),5,90)
        r=route(p,[5,7]);r['pickupArUcoId']=8
        with self.assertRaises(ValueError):validate_route(p,backend_map(p),r,5,-90)

    def test_dotnet_seven_fractional_digits_and_naive_expiry(self):
        self.assertEqual(utc('2026-11-28T00:00:00.1234567Z'),utc('2026-11-28T07:00:00.123456+07:00'))
        with self.assertRaises(ValueError):utc('2026-11-28T00:00:00')

    def test_mixed_world_export_keeps_physical_identity_outside_webots_and_matches_manifest(self):
        sys.path.insert(0,str(ROOT/'tools'))
        from build_physical_demo_scene import build
        from validate_simulation import validate
        with tempfile.TemporaryDirectory() as folder:
            generated=build(fixture_profile(),folder)
            validated=validate(folder,'config/physical-demo-manifest.json')
            self.assertEqual(len(validated['anchors']),9)
            self.assertEqual(validated['intersections'],[6])
            self.assertEqual(generated['robots'],[dict(code='R02',defName='SF_R02')])
            world=(Path(folder)/validated['world']).read_text()
            self.assertNotIn('DEF SF_R01',world)
            self.assertIn('--robot-code=R02',world)
            with self.assertRaises(ValueError):build(fixture_profile(),folder)


class PhysicalCameraMathTests(unittest.TestCase):
    def test_synthetic_calibrated_perspective_recovers_robot_origin_and_all_headings(self):
        import math
        import cv2
        import numpy as np
        from localization import Localizer
        for heading in (0,90,180,-90):
            p=fixture_profile()
            tilt=math.radians(35)
            mount=np.array([[0,-math.sin(tilt),math.cos(tilt)],[-1,0,0],[0,-math.cos(tilt),-math.sin(tilt)]])
            p['camera'].update(imageSize=[640,480],cameraMatrix=[[500,0,320],[0,500,240],[0,0,1]],
                distortionCoefficients=[0,0,0,0,0],robotFromOpticalRotation=mount.tolist(),robotFromOpticalTranslation=[.1,0,.18])
            angle=math.radians(heading)
            robot_rotation=np.array([[math.cos(angle),-math.sin(angle),0],[math.sin(angle),math.cos(angle),0],[0,0,1]])
            marker=next(m for m in p['markers'] if m['arucoId']==5)
            marker_position=np.array([-marker['row']*.3,-marker['col']*.3,0])
            robot_position=marker_position-robot_rotation@np.array([.45,0,0])
            camera_position=robot_position+robot_rotation@np.array([.1,0,.18])
            camera_from_map=(robot_rotation@mount).T
            rvec=cv2.Rodrigues(camera_from_map)[0]
            tvec=(camera_from_map@(marker_position-camera_position)).reshape(3,1)
            size=marker['markerSizeM']/2
            points=np.array([[-size,size,0],[size,size,0],[size,-size,0],[-size,-size,0]])
            corners=cv2.projectPoints(points,rvec,tvec,np.array(p['camera']['cameraMatrix'],dtype=float),np.zeros(5))[0]
            localizer=Localizer(p,cv2,np)
            sensors={'odom':dict(x=0,y=0,yaw=.73),'imu':{'yaw':1.2}}
            for i in range(5):localizer.vision([corners],[5],640,480,sensors,10+i*.05)
            pose=localizer.pose(sensors,10.21)
            self.assertIsNotNone(pose)
            self.assertAlmostEqual(pose['x'],robot_position[0],places=5)
            self.assertAlmostEqual(pose['y'],robot_position[1],places=5)
            self.assertAlmostEqual(abs((pose['headingDeg']-heading+180)%360-180),0,places=5)


if __name__=='__main__':unittest.main()
