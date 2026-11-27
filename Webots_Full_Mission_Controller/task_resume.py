"""Retained-session Pause/Resume. Never reconstructs a mission after restart."""
import copy
import math
import uuid


class PausedMission:
    def __init__(self, mission, pending, sim_time, wall_time, wheels, yaw, session, epoch):
        self.mission = mission
        self.pending = copy.deepcopy(pending)
        self.sim_time, self.wall_time = sim_time, wall_time
        self.wheels, self.yaw, self.epoch = tuple(wheels), yaw, epoch
        self.identity = tuple(mission[k] for k in ('taskId','assignmentId','missionId','mapRevision'))
        self.context = dict(controllerSessionId=session, mapRevision=mission['mapRevision'],
            nextWaypointIndex=mission['next_index'], phase=mission['phase'])
        self.pause_command_id = None

    def bind(self, command):
        self.pause_command_id = command['commandId']
        self.context['resumeToken'] = str(uuid.uuid4())

    def matches(self, command, mission, wheels, yaw, epoch):
        context = command.get('route')
        if mission is not self.mission or not isinstance(context,dict): return False
        if tuple(command.get(k) for k in ('taskId','assignmentId','missionId')) != self.identity[:3]: return False
        if mission.get('mapRevision') != self.identity[3] or epoch != self.epoch: return False
        if any(context.get(k) != self.context[k] for k in ('resumeToken','controllerSessionId','mapRevision')): return False
        if any(not math.isfinite(v) for v in (*wheels,yaw)): return False
        if max(abs(a-b) for a,b in zip(wheels,self.wheels))>.25: return False
        return abs((yaw-self.yaw+180)%360-180)<=5

    def restore(self, sim_time, wall_time, nav_request, ack_timeout, ack_retry):
        # Exclude only the stopped interval. Original encoder origin, waypoint,
        # phase, load verification and intersection ownership are untouched.
        sim_delta, wall_delta = sim_time-self.sim_time, wall_time-self.wall_time
        for key in ('segment_started_at',):
            if key in self.mission: self.mission[key] += sim_delta
        for key in ('sf_hold_until','heading_confirmation_since','verification_poll_at'):
            if key in self.mission: self.mission[key] += wall_delta
        if nav_request is not None:
            nav_request['deadline'] = wall_time+ack_timeout
            nav_request['retry_at'] = wall_time
        pending = self.pending
        if pending is not None and pending.get('accepted') is not True:
            pending['eventId'] = str(uuid.uuid4()); pending['body']['eventId'] = pending['eventId']
            pending.update(accepted=None, reason='', deadline=wall_time+ack_timeout, retry_at=wall_time)
        return pending, sim_delta
