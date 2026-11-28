"""Opt-in JetRacer mission runtime. No roslaunch, installs or notebook mutation."""
import datetime
import json
import math
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid
from localization import Localizer
from mission import MissionAgent
from protocol import utc, validate_map, validate_profile
from line_vision import MarkerLineVision,is_line_demo


class NativeIO:
    def __init__(self, profile, directory):
        import raw_camera
        self.p,self.folder,self.camera=profile,directory,raw_camera
        self.line_demo=is_line_demo(profile)
        self.latest={}
        self.vision_t=-1e9
        self.alive=False
        self.proc=self.bridge=self.reader=None
        self.desired=(0,0,-1e9)
        self.distance=0
        self.last_odom=None
        self.battery={}
        self.fault=None
        self.preview_jpeg=b''

    def start(self, observe_only=False):
        self.camera.load_cv()
        self.localizer=(MarkerLineVision(self.p,self.camera.cv2,self.camera.np) if self.line_demo
                        else Localizer(self.p,self.camera.cv2,self.camera.np))
        self.bridge=self.camera.NotebookBridge(self.folder)
        self.bridge.start()
        self.reader=self.camera.RawReader(str(self.bridge.path),max_age=.5)
        env=os.environ.copy()
        for key in ('PYTHONPATH','PYTHONHOME','PYTHONSTARTUP'):
            env.pop(key,None)
        env['SMARTFLEET_SCAN_YAW_RAD']=str(self.p['scanMountYawRad'])
        env['SMARTFLEET_MOTION_LIMIT_S']='50' if self.line_demo else '20'
        env['SMARTFLEET_REQUIRE_LIDAR']='false' if self.line_demo and self.p.get('supervisedNoLidar') else 'true'
        self.log=(self.folder/'ros-io.log').open('wb')
        shell='source /opt/ros/melodic/setup.bash && source "$HOME/catkin_ws/devel/setup.bash" && exec python2 -u "$@"'
        command=['bash','-c',shell,'smartfleet-physical',str(Path(__file__).with_name('ros_io_bridge.py'))]
        if observe_only:
            command.append('--observe-only')
        self.proc=subprocess.Popen(command,
            env=env,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=self.log,start_new_session=True)
        self.alive=True
        self.workers=[]
        for fn in (self.read_loop,self.camera_loop,self.command_loop,self.battery_loop):
            t=threading.Thread(target=fn)
            t.daemon=True
            t.start()
            self.workers.append(t)
        # ROS/tf imports exceed four seconds on the real Jetson. Keep motion
        # disarmed while waiting; this deadline does not relax the drive watchdog.
        deadline=time.monotonic()+20
        while time.monotonic()<deadline and not self.latest and not self.fault and self.proc.poll() is None:
            time.sleep(.02)
        if self.fault or not self.latest or self.proc.poll() is not None:
            detail=(self.folder/'ros-io.log').read_text(errors='replace').strip()[-2000:]
            message=self.fault or 'ROS IO startup timed out after 20 seconds (exit={})'.format(self.proc.poll())
            raise RuntimeError(message+('\nROS IO log:\n'+detail if detail else ''))

    def drive(self,v,steer):
        self.desired=(v,steer,time.monotonic())

    def command_loop(self):
        try:
            while self.alive:
                v,s,t=self.desired
                if time.monotonic()-t>.25:
                    if v:
                        self.fault='Mission control loop stalled; manual recovery required'
                    v=s=0
                if self.fault:
                    v=s=0
                self.proc.stdin.write((json.dumps(dict(v=v,steer=s,armed=bool(v)))+'\n').encode())
                self.proc.stdin.flush()
                time.sleep(.05)
        except Exception as ex:
            self.fault='ROS control link failed: '+str(ex)

    def read_loop(self):
        try:
            for line in iter(self.proc.stdout.readline,b''):
                try:
                    record=json.loads(line.decode())
                    if 't' not in record:
                        continue
                    self.latest=record
                    o=record.get('data',{}).get('odom')
                    if o and self.last_odom and o['stamp']!=self.last_odom['stamp']:
                        delta=math.hypot(o['x']-self.last_odom['x'],o['y']-self.last_odom['y'])
                        if delta>.12 and not self.line_demo:
                            self.fault='Encoder odometry discontinuity'
                        else:
                            self.distance+=delta
                    if o:
                        self.last_odom=o
                    if record.get('reason') in ('CONTINUOUS_MOTION_LIMIT','CONTROL_HEARTBEAT_TIMEOUT') and self.desired[0]:
                        self.fault='ROS watchdog stopped the mission: '+record['reason']
                except ValueError:
                    continue
        finally:
            if self.alive:
                self.fault='ROS IO process disconnected'

    def camera_loop(self):
        detector=self.camera.Detector(tuned=True)
        log=(self.folder/'aruco.jsonl').open('a',buffering=1)
        preview_at=0
        try:
            while self.alive:
                self.bridge.check()
                frame=self.reader.latest()
                data=self.latest.get('data',{})
                if frame is None or 'odom' not in data or 'imu' not in data:
                    time.sleep(.01)
                    continue
                begin=time.monotonic()
                corners,ids,_=detector.run(frame['gray'])
                ids=[] if ids is None else ids.flatten().tolist()
                if self.line_demo:self.localizer.vision(frame['gray'],corners,ids,begin)
                else:self.localizer.vision(corners,ids,frame['gray'].shape[1],frame['gray'].shape[0],data,begin)
                self.vision_t=time.monotonic()
                if self.vision_t-preview_at>.2:
                    cv,np=self.camera.cv2,self.camera.np
                    preview=cv.cvtColor(frame['gray'],cv.COLOR_GRAY2BGR)
                    if ids:cv.aruco.drawDetectedMarkers(preview,corners,np.asarray(ids,dtype=np.int32).reshape(-1,1))
                    if self.line_demo:
                        line=self.localizer.line
                        cv.putText(preview,'Line: '+str(line.get('visible'))+' | error: '+str(line.get('error')),
                            (8,24),cv.FONT_HERSHEY_SIMPLEX,.5,(80,220,80),1)
                    okay,jpeg=cv.imencode('.jpg',preview,[cv.IMWRITE_JPEG_QUALITY,75])
                    if okay:self.preview_jpeg=jpeg.tobytes()
                    preview_at=self.vision_t
                log.write(json.dumps(dict(t=begin,seq=frame['seq'],ids=ids,
                    corners=[c.reshape(4,2).tolist() for c in corners],headerAgeMs=frame['header_age_ms'],
                    markers=self.localizer.markers))+'\n')
                time.sleep(max(0,.05-(time.monotonic()-begin)))
        except Exception as ex:
            self.fault='Camera/localization failed: '+str(ex)
        finally:
            log.close()

    def battery_loop(self):
        try:
            from jetcard.ina219 import INA219
            ina=INA219(addr=self.p['battery']['address'])
            full,empty=self.p['battery']['fullVoltage'],self.p['battery']['emptyVoltage']
            while self.alive:
                bus=float(ina.getBusVoltage_V())
                voltage=bus+float(ina.getShuntVoltage_mV())/1000
                if not math.isfinite(voltage) or not 0<voltage<30:
                    raise ValueError('Invalid battery voltage')
                self.battery=dict(t=time.monotonic(),batteryVoltage=voltage,
                    batteryPercent=max(0,min(100,100*(bus-empty)/(full-empty))))
                time.sleep(1)
        except Exception as ex:
            self.battery={}
            self.fault='Battery sensor failed: '+str(ex)

    def sample(self):
        now=time.monotonic()
        record=self.latest
        data=record.get('data',{})
        pose=self.localizer.pose(data,now) if not self.line_demo and 'odom' in data and 'imu' in data else None
        required=('odom','imu','camera') if self.line_demo and self.p.get('supervisedNoLidar') else ('odom','imu','camera','lidar')
        fresh=(now-record.get('t',-1e9)<.3 and all(0<=record.get('ages',{}).get(k,999)<.5 for k in required))
        battery=self.battery if now-self.battery.get('t',-1e9)<3 else {}
        result=dict(t=now,healthy=bool(fresh and not self.fault and now-self.vision_t<.4),poseValid=bool(pose),
            stationary=abs(data.get('odom',{}).get('v',999))<.015 and abs(data.get('imu',{}).get('wz',999))<.08,
            markers=dict(self.localizer.markers),lidar=data.get('lidar',{'status':'NO_SCAN_DATA','distanceCm':None}),
            encoderDistanceM=self.distance,linearVelocity=data.get('odom',{}).get('v'),
            angularVelocity=data.get('imu',{}).get('wz'))
        result.update({k:v for k,v in battery.items() if k!='t'})
        if self.line_demo:
            result.update(line=dict(self.localizer.line),localMotion={k:data.get('odom',{}).get(k) for k in ('x','y','yaw')})
            if any(result['localMotion'][k] is None for k in ('x','y','yaw')):result['localMotion']=None
        if pose:
            result.update(pose)
        return result

    def close(self):
        self.drive(0,0)
        self.alive=False
        if self.proc and self.proc.poll() is None:
            try:
                self.proc.stdin.write(b'{"armed":false,"v":0,"steer":0}\n')
                self.proc.stdin.flush()
                self.proc.stdin.close()
                self.proc.wait(timeout=3)
            except Exception:
                self.proc.terminate()
        for t in getattr(self,'workers',[]):
            t.join(timeout=2)
        if self.bridge:
            self.bridge.close()
        if self.reader:
            self.reader.close()
        if hasattr(self,'log'):
            self.log.close()


class PhysicalRuntime:
    def __init__(self,profile_path):
        self.p=json.loads(Path(profile_path).read_text())
        validate_profile(self.p)  # Before importing hardware packages or creating publishers.
        if (self.p.get('scanMountYawRad') is None or (not is_line_demo(self.p) and not self.p['battery'].get('validated')) or
            not 0<self.p['battery'].get('emptyVoltage',0)<self.p['battery'].get('fullVoltage',0)<30):
            raise ValueError('LiDAR mounting and battery scale must be verified')
        folder=Path.home()/'.smartfleet'/'physical'/self.p['robotCode']
        self.folder=folder/('run-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:6])
        self.folder.mkdir(parents=True)
        self.io=NativeIO(self.p,self.folder)
        self.messages=queue.Queue(maxsize=256)
        self.running=False
        self.clock_anchor=None
        self.map_request=None
        self.run_id=str(uuid.uuid4())
        self.sequence=0
        self.agent=MissionAgent(self.p,self.publish,self.io.drive,folder/'mission-hold.json',server_now=self.server_now)

    def server_now(self):
        if not self.clock_anchor:
            raise ValueError('Backend clock reference unavailable')
        return self.clock_anchor[0]+time.monotonic()-self.clock_anchor[1]

    def publish(self,topic,data):
        self.client.publish(topic,json.dumps(data,allow_nan=False),qos=1,retain=False)

    def start(self):
        import paho.mqtt.client as mqtt
        # Explicit identity must match the Admin registry, and differ from all Webots identities.
        self.client=mqtt.Client(client_id=self.p['mqttClientId'],clean_session=True)
        self.client.reconnect_delay_set(1,5)
        def connected(cli,userdata,flags,rc,*_):
            try:self.messages.put_nowait(('connect',dict(rc=int(getattr(rc,'value',rc)))))
            except queue.Full:
                self.io.fault='MQTT queue overflow'
                return
            for name in ('command','status-ack','qr-progress-ack','nav-control','navigation-ack','localization-map'):
                cli.subscribe(self.agent.topic(name),qos=1)
        def disconnected(*_):
            self.io.drive(0,0)
            self.agent.connected=False
            try:self.messages.put_nowait(('disconnect',{}))
            except queue.Full:self.io.fault='MQTT queue overflow'
        def message(cli,userdata,msg):
            if msg.retain or len(msg.payload)>65536:
                return
            try:
                data=json.loads(msg.payload.decode())
                if not isinstance(data,dict):return
                suffix=msg.topic.rsplit('/',1)[-1]
                if (suffix=='command' and data.get('command')!='EXECUTE_TASK' and self.agent.route and
                    all(data.get(k)==self.agent.route[k] for k in ('taskId','assignmentId','missionId')) and
                    data.get('commandId') not in self.agent.commands):
                    self.io.drive(0,0)
                self.messages.put_nowait((suffix,data))
            except (ValueError,queue.Full):
                self.io.fault='Invalid MQTT message or event queue overflow'
        self.client.on_connect=connected
        self.client.on_disconnect=disconnected
        self.client.on_message=message
        try:self.io.start()
        except Exception:
            self.io.close()
            raise
        self.client.connect_async(self.p['brokerHost'],self.p['brokerPort'],keepalive=15)
        self.client.loop_start()
        self.running=True
        self.thread=threading.Thread(target=self.loop)
        self.thread.daemon=True
        self.thread.start()
        print('Physical agent running:',self.p['robotCode'],'| journal:',self.agent.path,'| state:',self.agent.state)
        return self

    def request_map(self):
        event=str(uuid.uuid4())
        self.map_request=(event,time.monotonic())
        self.publish(self.agent.topic('qr-progress'),dict(event='LOCALIZATION_CONFIG',eventId=event))

    def receive(self,suffix,data):
        a=self.agent
        if suffix=='connect':
            a.connected=data['rc']==0
            a.ready=False
            if a.route:
                a.fail('MQTT reconnect/restart during mission; physical recovery required')
            if a.connected:self.request_map()
        elif suffix=='disconnect':
            a.ready=False
            if a.route:a.fail('MQTT disconnected; retain mission/load/traffic locks')
        elif suffix=='localization-map':
            if not self.map_request or data.get('eventId')!=self.map_request[0]:return
            elapsed=time.monotonic()-self.map_request[1]
            if not 0<=elapsed<=1:return
            if is_line_demo(self.p) and data.get('lineDemoAllowed') is not True:
                raise ValueError('Enable this PHYSICAL robot in backend PhysicalRobots:LineDemoRobotCodes; stop/return-to-charge mode must be disabled for this demo')
            validate_map(self.p,data['map'])
            if a.route and data['map']['mapRevision']!=a.route['mapRevision']:
                a.fail('Map revision changed during physical mission')
                return
            # Conservative clock bound; never modify Jetson system time.
            self.clock_anchor=(utc(data['serverTimeUtc'])+elapsed,time.monotonic())
            a.map=data['map']
            self.map_request=None
        elif suffix=='nav-control':
            # Navigation traffic may arrive before LOCALIZATION_CONFIG.
            # No directive can be timed safely until the backend clock is known.
            if self.clock_anchor is None:
                self.io.drive(0,0)
                return
            a.nav(data)
        elif suffix=='command':
            try:a.on_command(data)
            except ValueError as exc:
                self.io.drive(0,0)
                if data.get('command')=='EXECUTE_TASK' and all(type(data.get(k)) is int for k in ('taskId','assignmentId','missionId')):
                    self.publish(a.topic('status'),dict(event='MISSION_FAILED',eventId=str(uuid.uuid4()),
                        taskId=data['taskId'],assignmentId=data['assignmentId'],missionId=data['missionId'],reason='Physical route rejected: '+str(exc)))
                print('[REJECTED]',str(exc))
        else:a.ack(suffix,data)

    def loop(self):
        telemetry_at=pose_at=map_at=0
        log=(self.folder/'events.jsonl').open('a',buffering=1)
        try:
            while self.running:
                for _ in range(64):
                    try:suffix,data=self.messages.get_nowait()
                    except queue.Empty:break
                    log.write(json.dumps(dict(t=time.monotonic(),topic=suffix,data=data))+'\n')
                    self.receive(suffix,data)
                sample=self.io.sample()
                if self.agent.ready and (not sample.get('healthy') or (not is_line_demo(self.p) and not sample.get('poseValid'))):
                    self.agent.ready=False
                    if self.agent.pending and self.agent.pending['kind']=='READY':self.agent.pending=None
                    if self.agent.connected:
                        self.publish(self.agent.topic('qr-progress'),dict(event='LOCALIZATION_LOST',eventId=str(uuid.uuid4())))
                if self.io.fault and self.agent.route and self.agent.state!='FAILED':self.agent.fail(self.io.fault)
                self.agent.tick(sample)
                now=time.monotonic()
                if self.agent.connected and now>=map_at:
                    self.request_map()
                    map_at=now+5
                if self.agent.connected and now>=telemetry_at:
                    telemetry={k:sample[k] for k in ('batteryPercent','batteryVoltage','linearVelocity','angularVelocity','encoderDistanceM') if sample.get(k) is not None}
                    if sample.get('poseValid'):
                        telemetry.update(positionX=sample['x'],positionY=sample['y'],heading=sample['headingDeg'])
                    self.publish(self.agent.topic('telemetry'),telemetry)
                    self.publish(self.agent.topic('lidar'),sample['lidar'] if sample['healthy'] else dict(status='NO_SCAN_DATA',distanceCm=None))
                    telemetry_at=now+1
                if self.agent.connected and self.agent.map and self.clock_anchor and sample.get('poseValid') and now>=pose_at:
                    scale=self.p['metersPerGridCell']
                    self.sequence+=1
                    self.publish(self.agent.topic('pose'),dict(robotCode=self.p['robotCode'],runId=self.run_id,sequence=self.sequence,
                        mapRevision=self.agent.map['mapRevision'],source='ARUCO_IMU_ODOMETRY',frame='GRID_COL_ROW',
                        x=-sample['y']/scale,y=-sample['x']/scale,yaw=sample['headingDeg'],
                        capturedAtUtc=datetime.datetime.utcfromtimestamp(self.server_now()).isoformat()+'Z'))
                    pose_at=now+.2
                time.sleep(.05)
        except Exception as exc:
            self.io.drive(0,0)
            self.agent.fail('Physical runtime error: '+str(exc))
            print('[STOPPED]',str(exc))
        finally:
            self.running=False
            self.io.drive(0,0)
            log.close()

    def close(self):
        if self.agent.route and self.agent.state not in ('FAILED','RECOVERY_HOLD'):
            self.agent.fail('Operator closed physical controller; physical recovery required')
        self.running=False
        if hasattr(self,'thread'):self.thread.join(timeout=3)
        self.io.close()
        if hasattr(self,'client'):
            self.client.disconnect()
            self.client.loop_stop()
        print('Physical controller stopped; existing ROS terminals unchanged')
