"""Main Flow 3/4 over the existing backend protocol, with injected hardware IO."""
import copy
import hashlib
import json
import math
import time
import uuid
from pathlib import Path
from protocol import HEADINGS, utc, validate_route, wrap
from line_vision import is_line_demo


class MissionAgent:
    def __init__(self, profile, publish, drive, journal, monotonic=time.monotonic, server_now=None):
        self.p, self.publish, self.drive = profile, publish, drive
        self.line_demo=is_line_demo(profile)
        self.now, self.server_now = monotonic, server_now
        self.path = Path(journal)
        self.code = profile['robotCode']
        self.session = str(uuid.uuid4())
        self.state = 'RECOVERY_HOLD' if self.path.exists() else 'IDLE'
        self.route = self.map = self.pending = self.directive = None
        self.index = 0
        self.current = None
        self.protected = None
        self.epoch = None
        self.nav_sequence = -1
        self.ready = False
        self.connected = False
        self.sample = {}
        self.last_ready = self.refresh_at = self.verify_at = -1e9
        self.error = None
        self.communication_warning = None
        self.commands = {}
        self.paused = None
        if self.path.exists():
            saved=json.loads(self.path.read_text())
            self.route=saved.get('route')
            if self.route:
                self.route_hash=hashlib.sha256(json.dumps(self.route,sort_keys=True).encode()).hexdigest()
                self.protected=saved.get('protected')
            # Identity is kept only for STOP/recovery ACKs, never to restart motion.

    def topic(self, suffix):
        return 'smartfleet/robot/{}/{}'.format(self.code, suffix)

    def persist(self):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix('.tmp')
        temp.write_text(json.dumps({'state':self.state, 'route':self.route, 'nextWaypointIndex':self.index,
                                    'protected':self.protected, 'session':self.session, 'error':self.error}, indent=2))
        temp.replace(self.path)

    def stop(self):
        self.drive(0, 0)

    def send(self, kind, suffix, body):
        self.stop()
        if self.pending:
            raise RuntimeError('An application ACK is still pending')
        body = dict(body, eventId=str(uuid.uuid4()))
        self.pending = dict(kind=kind, suffix=suffix, body=body, deadline=self.now()+10, retry=self.now()+1)
        self.publish(self.topic(suffix), body)

    def status(self, event, **extra):
        body = {k:self.route[k] for k in ('taskId','assignmentId','missionId')}
        body.update(event=event, **extra)
        self.send(event, 'status', body)

    def progress(self, event='WAYPOINT_REACHED', index=None):
        i = self.index if index is None else index
        self.send(event, 'qr-progress', dict(event=event, missionId=self.route['missionId'],
            mapRevision=self.route['mapRevision'], arucoId=self.route['waypoints'][i]['arucoId'],
            waypointIndex=i, robotStopped=True, headingDeg=self.sample.get('headingDeg'),
            localizationSource='ARUCO_LINE_DEMO' if self.line_demo else 'ARUCO_IMU'))

    def fail(self, why):
        self.stop()
        self.ready = False
        self.communication_warning = None
        self.error = why
        self.pending = None
        self.state = 'FAILED'
        if self.route:
            self.persist()
            if self.connected:
                self.status('MISSION_FAILED', reason=why)

    def sensor_safe(self, moving=True):
        s = self.sample
        lidar=s.get('lidar',{}).get('status')
        supervised=(self.line_demo and self.p.get('supervisedNoLidar') is True and lidar=='NO_SCAN_DATA' and
            (not self.route or not any(w['isIntersection'] for w in self.route['waypoints'])))
        common=(self.connected and s.get('healthy') is True and 0<=self.now()-s.get('t',-1e9)<.3 and
                (supervised or lidar in (('CLEAR','DETECTED') if moving else ('CLEAR','DETECTED','CRITICAL'))) and
                type(s.get('batteryPercent')) in (int,float) and s['batteryPercent']>=10)
        if self.line_demo:return common
        return (common and s.get('poseValid') is True and
                all(type(s.get(k)) in (int,float) and math.isfinite(s[k]) for k in ('x','y','headingDeg')))

    def at(self, marker_id):
        observation = self.sample.get('markers', {}).get(marker_id)
        return (bool(observation) and observation.get('stableFrames',0)>=(self.p['markerConfirmFrames'] if self.line_demo else 5) and
                0<=self.now()-observation.get('t',-1e9)<.3 and
                (self.line_demo or observation.get('distanceM',999)<=self.p['arrivalRadiusM']) and
                self.sample.get('stationary') is True)

    def on_command(self, data, retained=False):
        if retained:
            return
        kind = data.get('command')
        if kind!='EXECUTE_TASK':
            return self.control(data)
        self.stop()
        digest = hashlib.sha256(json.dumps(data,sort_keys=True).encode()).hexdigest()
        if self.route:
            if data.get('missionId')==self.route['missionId'] and digest==self.route_hash:
                return  # QoS/outbox retry cannot reset an executing mission.
            self.fail('Conflicting mission delivery while physical context is held')
            raise ValueError('Another mission/context is already held')
        if self.state!='IDLE' or not self.ready or not self.sensor_safe(False) or not self.at(self.current):
            raise ValueError('Fresh stopped camera/IMU READY and healthy sensors required')
        direction = validate_route(self.p, self.map, data, self.current, self.sample.get('headingDeg'))
        if self.line_demo and self.p.get('supervisedNoLidar') and any(w['isIntersection'] for w in data['waypoints']):
            raise ValueError('Supervised no-LiDAR demo supports routes outside reserved intersections only')
        if self.line_demo and hasattr(self,'direction') and direction!=self.direction:
            raise ValueError('Reposition/reorient the car for the opposite line before starting a new controller session; no automatic U-turn')
        self.direction=direction
        self.route, self.route_hash = copy.deepcopy(data), digest
        self.index = 0
        # Idle READY retry is superseded only after the mission validates.
        self.pending = None
        self.state = 'START_ACK'
        self.persist()
        self.status('MISSION_STARTED')

    def control(self, data):
        if not self.route or any(data.get(k)!=self.route[k] for k in ('taskId','assignmentId','missionId')):
            return
        cid, kind = data.get('commandId'), data.get('command')
        if not isinstance(cid,str) or not cid:
            return
        if cid in self.commands:
            self.publish(self.topic('status'), self.commands[cid])
            return
        self.stop()
        if kind in ('PAUSE_TASK','CANCEL_STOP','FORCE_CANCEL'):
            if not self.sample.get('stationary'):
                # Do not ACK a physical stop until actual encoders/IMU settle.
                if self.state!='STOPPING_CONTROL':
                    self.state_before_stop = self.state
                    self.stopping_pending = self.pending
                    self.pending = None
                self.stopping_control = dict(data)
                self.state = 'STOPPING_CONTROL'
                return
            saved_state = self.state_before_stop if self.state=='STOPPING_CONTROL' else self.state
            if hasattr(self,'state_before_stop'):
                del self.state_before_stop
            if hasattr(self,'stopping_pending'):
                self.pending=self.stopping_pending
                del self.stopping_pending
            context = dict(controllerSessionId=self.session,resumeToken=str(uuid.uuid4()),
                mapRevision=self.route['mapRevision'],nextWaypointIndex=self.index,phase=saved_state)
            if kind=='PAUSE_TASK' and self.paused is None:
                self.paused = dict(state=saved_state,pending=self.pending,context=context,
                                   pose=(self.sample.get('x'),self.sample.get('y'),self.sample.get('headingDeg')),epoch=self.epoch,
                                   localMotion=copy.deepcopy(self.sample.get('localMotion')))
            elif kind=='PAUSE_TASK':
                context=self.paused['context']
            else:
                self.paused=None
            self.pending = None
            self.state = 'PAUSED' if kind=='PAUSE_TASK' else 'RECOVERY_HOLD'
            self.persist()
            self.status('CONTROL_STOPPED',commandId=cid,currentArUcoId=self.current,
                        **({'resumeContext':context} if kind=='PAUSE_TASK' else {}))
            self.commands[cid] = self.pending['body']
        elif kind=='RESUME_TASK':
            context = data.get('route', {})
            saved = self.paused
            okay = self.state=='PAUSED' and saved and context==saved['context'] and saved['epoch']==self.epoch and self.sensor_safe() and self.sample.get('stationary')
            if okay:
                if self.line_demo:
                    old,current=saved.get('localMotion'),self.sample.get('localMotion')
                    okay=(old is not None and current is not None and
                        abs(current['x']-old['x'])<.005 and abs(current['y']-old['y'])<.005 and
                        abs(wrap(math.degrees(current['yaw']-old['yaw'])))<5)
                else:
                    x,y,h=saved['pose']
                    okay=math.hypot(self.sample['x']-x,self.sample['y']-y)<.05 and abs(wrap(self.sample['headingDeg']-h))<5
            if self.pending:
                return
            if not okay:
                self.status('CONTROL_RESUME_REJECTED',commandId=cid,reason='RESUME_UNSAFE' if saved else 'RESUME_CONTEXT_LOST')
            else:
                self.state='RESUME_ACK'
                self.status('CONTROL_RESUMED',commandId=cid,resumeContext=saved['context'])
            self.commands[cid]=self.pending['body']
        elif kind=='RETURN_HOME':
            self.fail('Physical return/charging turns are not commissioned; hold for Maintenance')

    def nav(self, data):
        if data.get('robotCode')!=self.code:
            return
        if self.state in ('RECOVERY_HOLD','FAILED'):
            return
        epoch, seq = data.get('epoch'), data.get('sequence')
        if not isinstance(epoch,str) or not epoch or type(seq) is not int:
            return
        if self.epoch and epoch!=self.epoch and self.route:
            self.fail('Backend navigation epoch changed; retain physical locks for recovery')
            return
        if epoch==self.epoch and seq<=self.nav_sequence:
            return
        self.epoch,self.nav_sequence=epoch,seq
        if not self.server_now:
            return
        remaining=utc(data.get('expiresAtUtc'))-self.server_now()
        if not 0<remaining<=3.5:
            self.directive=None
            self.stop()
            return
        self.directive=dict(data,deadline=self.now()+remaining)
        if data.get('mode')=='STOP':
            self.stop()

    def permission(self):
        d=self.directive
        if not d or self.now()>=d['deadline'] or d.get('mode') not in ('GO','SLOW'):
            return False
        if not self.route or not 0<self.index<len(self.route['waypoints']):
            return False
        target=self.route['waypoints'][self.index]
        protected=target['arucoId'] if target['isIntersection'] else self.protected
        return (type(d.get('maxLinearSpeed')) in (float,int) and 0<d['maxLinearSpeed']<=5 and
                (d.get('intersectionArUcoId') is None if protected is None else
                 d.get('mode')=='GO' and d.get('intersectionGranted') is True and d.get('intersectionArUcoId')==protected))

    def nav_send(self, kind, **payload):
        self.state='NAV_ACK'
        self.send(kind,'scan' if kind=='SCAN' else 'navigation',payload if kind=='SCAN' else dict(payload,event=kind))

    def begin_segment(self):
        self.stop()
        target=self.route['waypoints'][self.index]
        if self.protected is not None:
            self.state='NAV_GO'
        elif target['isIntersection']:
            self.nav_send('APPROACH',intersectionArUcoId=target['arucoId'],currentArUcoId=self.current)
        else:
            following=self.route['waypoints'][self.index+1]['arucoId'] if self.index+1<len(self.route['waypoints']) else None
            self.nav_send('SCAN',currentArUcoId=self.current,nextArUcoId=target['arucoId'],followingArUcoId=following)
        self.refresh_at=self.now()+2

    def ack(self, suffix, data):
        p=self.pending
        expected={'status':'status-ack','qr-progress':'qr-progress-ack','scan':'navigation-ack','navigation':'navigation-ack'}
        if not p or suffix!=expected[p['suffix']] or data.get('eventId')!=p['body']['eventId']:
            return
        if p['suffix']=='status' and data.get('assignmentId')!=self.route['assignmentId']:
            return
        if p['suffix'] in ('scan','navigation') and (data.get('robotCode')!=self.code or data.get('kind')!=p['kind']):
            return
        if p['suffix']=='qr-progress' and p['kind']!='READY' and data.get('missionId')!=self.route['missionId']:
            return
        self.pending=None
        self.communication_warning=None
        kind=p['kind']
        if data.get('accepted') is not True:
            if kind=='READY':
                self.ready=False
            elif self.state not in ('PAUSED','RECOVERY_HOLD','FAILED'):
                self.fail('Backend rejected '+kind+': '+str(data.get('reason',data.get('result'))))
            return
        if kind=='READY':
            self.ready=True
        elif kind=='MISSION_STARTED':
            self.state='PROGRESS_ACK'
            self.progress()
        elif kind=='WAYPOINT_REACHED':
            reached=self.index
            self.current=self.route['waypoints'][reached]['arucoId']
            self.index+=1
            self.persist()
            if reached in (self.route['pickupWaypointIndex'],len(self.route['waypoints'])-1):
                self.state='VERIFY'
                self.verify_at=self.now()
            else:
                self.begin_segment()
        elif kind=='VERIFICATION_STATUS':
            action=data.get('action')
            # A delayed reply cannot authorize departure or completion using an
            # old camera observation. Poll again once stopped presence is fresh.
            if action in ('CONTINUE','COMPLETE') and (not self.sensor_safe(False) or not self.at(self.current)):
                self.state='VERIFY'
                self.verify_at=self.now()
                return
            if action=='CONTINUE' and self.index<len(self.route['waypoints']):
                self.begin_segment()
            elif action=='COMPLETE' and self.index==len(self.route['waypoints']):
                self.state='COMPLETE_ACK'
                self.status('MISSION_COMPLETED')
            elif action not in ('WAIT_PICKUP_VERIFICATION','WAIT_DELIVERY_VERIFICATION'):
                self.fail('Invalid verification action')
            self.verify_at=self.now()+2
        elif kind in ('SCAN','APPROACH'):
            self.state='NAV_GO'
        elif kind=='ENTER':
            self.protected=p['body']['intersectionArUcoId']
            self.state='NAV_GO'
        elif kind=='EXIT':
            self.protected=None
            self.state='PROGRESS_ACK'
            self.progress()
        elif kind=='MISSION_COMPLETED':
            self.route=None
            self.state='IDLE'
            self.ready=False
            self.path.unlink()
        elif kind=='CONTROL_RESUMED':
            saved=self.paused
            self.state=saved['state']
            self.pending=saved['pending']
            if self.pending:
                self.pending.update(deadline=self.now()+10,retry=self.now())
            if self.state=='MOVING':
                self.segment_started=self.now()
            self.directive=None  # a fresh grant is required before resumed motion.
            self.paused=None
            self.persist()

    def tick(self, sample):
        self.sample=sample
        self.stop()
        if self.line_demo and self.state=='IDLE' and self.ready and not self.at(self.current):
            self.ready=False
            if self.pending and self.pending['kind']=='READY':self.pending=None
            if self.connected:
                self.publish(self.topic('qr-progress'),dict(event='LOCALIZATION_LOST',eventId=str(uuid.uuid4())))
        if self.state=='STOPPING_CONTROL' and sample.get('stationary'):
            command=self.stopping_control
            del self.stopping_control
            self.control(command)
            return
        if self.pending:
            if self.now()>=self.pending['deadline']:
                if self.pending['kind']=='VERIFICATION_STATUS' and self.state=='VERIFY':
                    # Waiting for an operator has no execution deadline. A slow
                    # cloud ACK keeps the same request and task safely stopped.
                    self.communication_warning='Waiting for backend confirmation; robot remains stopped.'
                    self.pending['deadline']=self.now()+10
                elif self.pending['kind']=='READY':
                    self.pending=None
                    self.ready=False
                elif self.state not in ('FAILED','PAUSED','RECOVERY_HOLD'):
                    self.fail('Application ACK timeout waiting for {}; physical state held'.format(self.pending['kind']))
                if not self.pending or self.state in ('FAILED','PAUSED','RECOVERY_HOLD'):
                    return
            if self.pending['kind']=='VERIFICATION_STATUS' and (not self.sensor_safe(False) or not self.at(self.current)):
                return  # Never refresh station presence from a stale/lost marker.
            if self.connected and self.now()>=self.pending['retry']:
                self.publish(self.topic(self.pending['suffix']),self.pending['body'])
                self.pending['retry']=self.now()+(2 if self.pending['kind']=='VERIFICATION_STATUS' else 1)
            return
        if self.state=='IDLE':
            if not self.map or not self.sensor_safe(False) or self.now()-self.last_ready<2:
                return
            candidates=[(k,v) for k,v in sample.get('markers',{}).items() if self.at(k)]
            if self.line_demo and len(candidates)>1:
                candidates.sort(key=lambda pair:pair[1].get('areaPx',0),reverse=True)
                if candidates[0][1].get('areaPx',0)>1.5*candidates[1][1].get('areaPx',0):candidates=candidates[:1]
            if len(candidates)!=1:
                self.ready=False
                return
            self.current,obs=candidates[0]
            body=dict(event='READY',arucoId=self.current,mapRevision=self.map['mapRevision'],robotStopped=True,
                headingDeg=sample.get('headingDeg'),localizationSource='ARUCO_LINE_DEMO' if self.line_demo else 'ARUCO_IMU',
                stableFrames=obs['stableFrames'])
            if not self.line_demo:body['reprojectionErrorPx']=obs['reprojectionErrorPx']
            self.send('READY','qr-progress',body)
            self.last_ready=self.now()
            return
        if self.state in ('PAUSED','FAILED','RECOVERY_HOLD','STOPPING_CONTROL'):
            return
        if not self.sensor_safe(False):
            return
        if self.state=='VERIFY':
            if self.now()>=self.verify_at and self.at(self.current):
                self.progress('VERIFICATION_STATUS',self.index-1)
            return
        if self.state=='NAV_GO':
            if self.sensor_safe() and self.permission():
                target=self.route['waypoints'][self.index]
                if target['isIntersection'] and self.protected is None:
                    self.nav_send('ENTER',intersectionArUcoId=target['arucoId'])
                else:
                    self.state='MOVING'
                    self.segment_started=self.now()
            elif self.protected is None and self.now()>=self.refresh_at:
                self.begin_segment()
            return
        if self.state!='MOVING':
            return
        if self.now()-self.segment_started>self.p['segmentTimeoutSeconds']:
            self.fail('Expected QR not reached before segment timeout')
            return
        target=self.route['waypoints'][self.index]
        visible=sample.get('markers',{}).get(target['arucoId'],{})
        target_seen=(visible.get('stableFrames',0)>=1 and 0<=self.now()-visible.get('t',-1e9)<.3) if self.line_demo else (
            visible.get('stableFrames',0)>=5 and visible.get('distanceM',999)<=self.p['arrivalRadiusM'])
        # In marker mode a center tag cannot prove EXIT. The configured outgoing
        # tag must be seen after the center has left the current camera frame.
        if self.line_demo and self.protected is not None and not target['isIntersection'] and self.protected in sample.get('markers',{}):
            target_seen=False
        if target_seen:
            if not self.at(target['arucoId']):
                return  # wait for real wheel stop before reporting arrival.
            self.current=target['arucoId']
            if self.protected is not None and not target['isIntersection']:
                if not self.line_demo:
                    anchor=next(x for x in self.p['markers'] if x['arucoId']==self.protected)
                    scale=self.p['metersPerGridCell']
                    dx,dy=sample['x']+anchor['row']*scale,sample['y']+anchor['col']*scale
                    h=math.radians(HEADINGS[self.direction])
                    if dx*math.cos(h)+dy*math.sin(h)<self.p['intersectionClearanceM']:
                        self.fail('Body/clearance beyond intersection not proven; keep ownership')
                        return
                self.nav_send('EXIT',intersectionArUcoId=self.protected,currentArUcoId=self.current)
            else:
                self.state='PROGRESS_ACK'
                self.progress()
            return
        if not self.sensor_safe() or not self.permission():
            return
        if self.line_demo:
            line=sample.get('line',{})
            if not line.get('visible') or not 0<=self.now()-line.get('t',-1e9)<.3:
                self.line_lost_since=getattr(self,'line_lost_since',self.now())
                if self.now()-self.line_lost_since>self.p.get('lineLostTimeoutSeconds',5):
                    self.fail('Black line lost; stopped without inventing a waypoint')
                return
            if hasattr(self,'line_lost_since'):del self.line_lost_since
            self.drive(min(self.p['maxSpeedMps'],float(self.directive['maxLinearSpeed'])),line['steer'])
            return
        error=wrap(HEADINGS[self.direction]-sample['headingDeg'])
        if abs(error)>12:
            self.fail('Heading deviated from straight commissioned corridor')
            return
        speed=min(self.p['maxSpeedMps'],float(self.directive['maxLinearSpeed']))
        self.drive(speed,max(-.2,min(.2,math.radians(error)*.8)))
