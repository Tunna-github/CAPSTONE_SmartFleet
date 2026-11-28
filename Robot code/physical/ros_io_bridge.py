#!/usr/bin/env python2
"""Owned ROS adapter. Does not launch/reconfigure any existing ROS node."""
from __future__ import print_function
import ctypes
import errno
import fcntl
import json
import math
import os
import signal
import socket
import sys
import threading
import time
from io_safety import valid_command, reason


class Timespec(ctypes.Structure):
    _fields_ = [('seconds', ctypes.c_long), ('nanoseconds', ctypes.c_long)]


_libc = ctypes.CDLL(None)


def monotonic():
    value = Timespec()
    if _libc.clock_gettime(1, ctypes.byref(value)) != 0:
        raise RuntimeError('Monotonic clock unavailable')
    return value.seconds + value.nanoseconds / 1e9


def yaw(q):
    return math.atan2(2 * (q.w*q.z + q.x*q.y), 1 - 2*(q.y*q.y + q.z*q.z))


def main():
    # Lock is released by OS on exit. Never replaces another live controller.
    lockfile = open('/tmp/smartfleet-physical-agent.lock', 'a+')
    fcntl.flock(lockfile, fcntl.LOCK_EX | fcntl.LOCK_NB)
    import rospy
    import rosgraph
    from geometry_msgs.msg import Twist
    from nav_msgs.msg import Odometry
    from sensor_msgs.msg import Imu, Image, LaserScan
    from std_msgs.msg import Int32
    rospy.init_node('smartfleet_physical_agent', anonymous=True, disable_signals=True)
    socket.setdefaulttimeout(.2)
    # A stuck notebook must never block the control watchdog on stdout.
    flags = fcntl.fcntl(sys.stdout.fileno(), fcntl.F_GETFL)
    fcntl.fcntl(sys.stdout.fileno(), fcntl.F_SETFL, flags | os.O_NONBLOCK)
    if rospy.get_param('/use_sim_time', False):
        raise RuntimeError('Simulation time refused')
    master = rosgraph.Master(rospy.get_name())
    max_hold=float(os.environ.get('SMARTFLEET_MOTION_LIMIT_S','20'))
    require_lidar=os.environ.get('SMARTFLEET_REQUIRE_LIDAR','true')!='false'
    if not 0<max_hold<=50:raise ValueError('Invalid bounded motion limit')
    import tf
    transforms=tf.TransformListener()
    state = {'command': {}, 'last': -1e9, 'sensors': {}, 'data': {}, 'alive': True}
    guard = threading.RLock()

    def receive():
        try:
            # Python 2's file iterator may read ahead and delay small pipe packets.
            for line in iter(sys.stdin.readline, ''):
                if len(line) > 2048:
                    continue
                try:
                    data = json.loads(line)
                    if not valid_command(data.get('v'), data.get('steer')):
                        continue
                    with guard:
                        state['command'] = data
                        state['last'] = monotonic()
                except (ValueError, TypeError):
                    continue
        finally:
            state['alive'] = False

    def odom(msg):
        age = rospy.get_time()-msg.header.stamp.to_sec()
        if msg.header.stamp.to_sec() <= 0 or not 0 <= age <= .6:
            return
        q = msg.pose.pose.orientation
        values = [msg.pose.pose.position.x, msg.pose.pose.position.y, yaw(q), msg.twist.twist.linear.x]
        if any(math.isnan(x) or math.isinf(x) for x in values):
            return
        with guard:
            state['sensors']['odom'] = monotonic()
            state['data']['odom'] = dict(zip(('x', 'y', 'yaw', 'v'), values))
            state['data']['odom']['stamp'] = msg.header.stamp.to_sec()
            state['data']['odom']['frame'] = msg.header.frame_id

    def imu(msg):
        age = rospy.get_time()-msg.header.stamp.to_sec()
        if msg.header.stamp.to_sec() <= 0 or not 0 <= age <= .6:
            return
        values = [yaw(msg.orientation), msg.angular_velocity.z]
        if any(math.isnan(x) or math.isinf(x) for x in values):
            return
        with guard:
            state['sensors']['imu'] = monotonic()
            state['data']['imu'] = {'yaw': values[0], 'wz': values[1], 'stamp': msg.header.stamp.to_sec()}

    def camera(msg):
        age = rospy.get_time() - msg.header.stamp.to_sec()
        if msg.header.stamp.to_sec() <= 0 or not 0 <= age <= .6:
            return
        with guard:
            state['sensors']['camera'] = monotonic()

    def scan(msg):
        age=rospy.get_time()-msg.header.stamp.to_sec()
        if msg.header.stamp.to_sec()<=0 or not 0<=age<=.6:
            return
        setting=os.environ['SMARTFLEET_SCAN_YAW_RAD']
        if setting=='auto':
            try:
                _,rotation=transforms.lookupTransform('base_footprint',msg.header.frame_id,rospy.Time(0))
                mount=tf.transformations.euler_from_quaternion(rotation)[2]
            except (tf.LookupException,tf.ConnectivityException,tf.ExtrapolationException):return
        else:mount=float(setting)
        values=[]
        total=0
        for index,value in enumerate(msg.ranges):
            a=msg.angle_min+index*msg.angle_increment+mount
            if abs(math.atan2(math.sin(a),math.cos(a)))>math.radians(50):continue
            total+=1
            if math.isinf(value) and value>0:values.append(float(msg.range_max))
            elif not math.isnan(value) and msg.range_min<=value<=msg.range_max:values.append(value)
        if total<10 or len(values)<total*.8:return
        distance=min(values)
        lidar={'status':'CRITICAL' if distance<=.25 else 'DETECTED' if distance<=.5 else 'CLEAR',
               'distanceCm':distance*100 if distance<=.5 else None}
        with guard:
            state['sensors']['lidar']=monotonic()
            state['data']['lidar']=lidar

    def motor(msg, key):
        with guard:
            state['data'][key] = {'value': msg.data, 'received': monotonic()}

    rospy.Subscriber('/scan', LaserScan, scan, queue_size=1)
    rospy.Subscriber('/odom_raw', Odometry, odom, queue_size=1)
    rospy.Subscriber('/imu', Imu, imu, queue_size=1)
    rospy.Subscriber('/csi_cam_0/image_raw', Image, camera, queue_size=1, buff_size=4194304)
    for key in ('lset', 'rset', 'lvel', 'rvel'):
        rospy.Subscriber('/motor/' + key, Int32, motor, callback_args=key, queue_size=1)

    def topology():
        try:
            pubs, subs, _ = master.getSystemState()
            others = [node for topic, nodes in pubs if topic == '/cmd_vel' for node in nodes if node != rospy.get_name()]
            drivers = [node for topic, nodes in subs if topic == '/cmd_vel' for node in nodes]
            return not others and drivers == ['/jetracer']
        except Exception:
            return False

    # Refuse even publisher creation if someone else already controls the car.
    if not topology():
        raise RuntimeError('Existing /cmd_vel controller or missing /jetracer. Stop old driving notebook first.')
    observe_only = '--observe-only' in sys.argv
    pub = None if observe_only else rospy.Publisher('/cmd_vel', Twist, queue_size=1, latch=False)
    stdin_thread = threading.Thread(target=receive)
    stdin_thread.daemon = True
    stdin_thread.start()
    def stop_signal(*_):
        state['alive'] = False
    signal.signal(signal.SIGTERM, stop_signal)
    signal.signal(signal.SIGINT, stop_signal)
    blocked = None
    active = False
    moving_since = None
    next_topology = 0
    topology_ok = False
    try:
        while state['alive'] and not rospy.is_shutdown():
            now = monotonic()
            if now >= next_topology:
                topology_ok = topology()
                next_topology = now + .25
            with guard:
                command = dict(state['command'])
                last = state['last']
                sensors = dict(state['sensors'])
                data = json.loads(json.dumps(state['data']))
            # Callbacks can run during topology I/O: evaluate time AFTER snapshot.
            now = monotonic()
            moving = bool(command.get('armed') and command.get('v'))
            # Explicit disarm required after any safety stop.
            if not command.get('armed'):
                blocked = None
                moving_since = None
            if moving and moving_since is None:
                moving_since = now
            if not moving:
                moving_since = None
            failure = reason(now, last, sensors, topology_ok, moving_since, moving,max_hold=max_hold,require_lidar=require_lidar)
            if failure and command.get('armed'):
                blocked = failure
            allowed = bool(not observe_only and command.get('armed') and not failure and not blocked)
            v = command.get('v', 0) if allowed else 0
            steer = command.get('steer', 0) if allowed and v else 0
            msg = Twist()
            msg.linear.x = v
            msg.angular.z = steer
            # No drive publications at all until operator arms; then zeros on stop.
            if pub is not None and (allowed or active):
                pub.publish(msg)
            active = allowed
            result = {'t': now, 'data': data, 'ages': {k: now-vv for k, vv in sensors.items()},
                      'applied': {'v': v, 'steer': steer}, 'armed': allowed,
                      'reason': blocked or failure, 'topologyOK': topology_ok,
                      'command_age_s': now-last}
            try:
                os.write(sys.stdout.fileno(), (json.dumps(result, allow_nan=False)+'\n').encode('utf-8'))
            except OSError as exc:
                if exc.errno not in (errno.EAGAIN, errno.EWOULDBLOCK):
                    state['alive'] = False
            time.sleep(.05)
    finally:
        # Stop only our commands; never kill or restart user's driver/camera.
        if pub is not None:
            for _ in range(15):
                pub.publish(Twist())
                time.sleep(.02)
        rospy.signal_shutdown('Keyboard Lab closed')


if __name__ == '__main__':
    main()
