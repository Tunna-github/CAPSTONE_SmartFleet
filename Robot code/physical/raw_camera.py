"""Native ROS raw camera adapter derived from the user RangeLab scanner. No driving code."""
import os

import sys

import platform

import argparse

import csv

import fcntl

import json

import math

import mmap

import re

import struct

import shutil

import subprocess

import tempfile

import signal

import time

from pathlib import Path

MAGIC = b'SFARU001'

GLOBAL = struct.Struct('<8sII')

HEADER = struct.Struct('<QddIIIII')

DATA_OFFSET = 128

CHANNELS = {1: 3, 2: 3, 3: 1, 4: 4, 5: 4}

cv2 = None

np = None

def load_cv():
    global cv2, np
    if platform.machine().lower() in ('aarch64', 'arm64'):
        # Jetson Nano's installed OpenBLAS can choose an unsupported CPU kernel.
        # Set this before both the import probe and NumPy/OpenCV in Jupyter.
        os.environ['OPENBLAS_CORETYPE'] = 'ARMV8'
        os.environ['OPENBLAS_NUM_THREADS'] = '1'
        deps = os.path.realpath(os.path.expanduser('~/jetracer_aruco_py36'))
        if not os.path.isdir(os.path.join(deps, 'cv2')):
            raise RuntimeError('Existing OpenCV contrib folder not found: ' + deps)
        cached = sys.modules.get('cv2')
        if cached and not os.path.realpath(cached.__file__).startswith(deps + os.sep):
            raise RuntimeError('Wrong cv2 already loaded; restart Jupyter kernel.')
        sys.path[:] = [deps] + [x for x in sys.path if x != deps]
    # Verify in a separate process before importing native libraries in Jupyter.
    cmd = ('import sys,json;sys.path[:]=json.loads(sys.argv[1]);'
           'import numpy,cv2;assert hasattr(cv2,"aruco");'
           'print("[IMPORT OK]",numpy.__version__,cv2.__version__,cv2.__file__)')
    if sys.version_info[:2] < (3, 6):
        raise RuntimeError('Scanner requires Python 3.6 or later, got ' + sys.version)
    print('[CHECK] Python {} {} | verifying native imports'.format(sys.version.split()[0], platform.machine()), flush=True)
    result = subprocess.run([sys.executable, '-u', '-X', 'faulthandler', '-c', cmd, json.dumps(sys.path)],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
    print(result.stdout.decode('utf-8', 'replace'), flush=True)
    if result.returncode:
        raise RuntimeError('Native import subprocess failed: {}'.format(result.returncode))
    import cv2 as c
    import numpy as n
    cv2, np = c, n
    cv2.setNumThreads(2)

class RawReader(object):
    def __init__(self, path, max_age=0.5):
        self.path, self.max_age = path, max_age
        self.file = self.mem = self.inode = None
        self.last_key = None
        self.capacity = 0
        self.error = 'Waiting for raw bridge'
        self.next_check = 0.0

    def close(self):
        if self.mem is not None:
            self.mem.close()
        if self.file is not None:
            self.file.close()
        self.file = self.mem = self.inode = None
        self.last_key = None

    def ensure_open(self):
        now = time.monotonic()
        if now < self.next_check:
            return self.mem is not None
        self.next_check = now + .25
        try:
            st = os.stat(self.path)
            inode = (st.st_dev, st.st_ino)
            if self.mem is not None and self.inode == inode:
                return True
            self.close()
            self.file = open(self.path, 'rb')
            actual = os.fstat(self.file.fileno())
            self.inode = (actual.st_dev, actual.st_ino)
            self.mem = mmap.mmap(self.file.fileno(), 0, access=mmap.ACCESS_READ)
            magic, capacity, offset = GLOBAL.unpack_from(self.mem)
            if magic != MAGIC or offset != DATA_OFFSET or capacity <= 0 or len(self.mem) != offset + capacity:
                raise ValueError('Incompatible raw bridge header')
            self.capacity = capacity
            self.error = 'Waiting for first fresh frame'
            return True
        except (OSError, ValueError, struct.error) as exc:
            self.close()
            self.error = str(exc)
            return False

    def latest(self):
        if not self.ensure_open():
            return None
        try:
            fcntl.flock(self.file, fcntl.LOCK_SH | fcntl.LOCK_NB)
        except OSError:
            return None
        try:
            seq, received, captured, w, h, step, code, size = HEADER.unpack_from(self.mem, GLOBAL.size)
            key = (self.inode, seq)
            if seq == 0 or key == self.last_key:
                return None
            ch = CHANNELS.get(code)
            if not ch or not (16 <= w <= 8192 and 16 <= h <= 8192 and step >= w * ch and size == h * step <= self.capacity):
                self.error = 'Invalid raw frame dimensions or encoding'
                return None
            self.last_key = key
            age = time.time() - received
            if not -.2 <= age <= self.max_age:
                self.error = 'Stale bridge frame ({:.0f} ms)'.format(age * 1000)
                return None
            if captured > 0 and not -.2 <= time.time() - captured <= self.max_age:
                self.error = 'Stale ROS-header frame ({:.0f} ms)'.format((time.time() - captured) * 1000)
                return None
            raw = self.mem[DATA_OFFSET:DATA_OFFSET + size]  # Copy while holding shared lock.
        finally:
            fcntl.flock(self.file, fcntl.LOCK_UN)
        pixels = np.frombuffer(raw, np.uint8).reshape(h, step)[:, :w * ch]
        pixels = pixels.reshape(h, w, ch) if ch > 1 else pixels.reshape(h, w)
        if code == 3:
            gray = np.ascontiguousarray(pixels)
        else:
            conversion = {1: cv2.COLOR_BGR2GRAY, 2: cv2.COLOR_RGB2GRAY,
                          4: cv2.COLOR_RGBA2GRAY, 5: cv2.COLOR_BGRA2GRAY}[code]
            gray = cv2.cvtColor(pixels, conversion)
        self.error = ''
        return {'gray': gray, 'key': key, 'seq': seq, 'received': received, 'captured': captured,
                'bridge_age_ms': (time.time() - received) * 1000,
                'header_age_ms': (time.time() - captured) * 1000 if captured > 0 else None}

class Detector(object):
    def __init__(self, tuned=False):
        a = cv2.aruco
        self.dictionary = (a.getPredefinedDictionary(a.DICT_4X4_50) if hasattr(a, 'getPredefinedDictionary')
                           else a.Dictionary_get(a.DICT_4X4_50))
        self.parameters = a.DetectorParameters_create() if hasattr(a, 'DetectorParameters_create') else a.DetectorParameters()
        if tuned:
            # Hypotheses for comparison, not universal improvements. Keep correction rate/default border checks.
            for k, v in {'adaptiveThreshWinSizeMin': 3, 'adaptiveThreshWinSizeMax': 35,
                         'adaptiveThreshWinSizeStep': 8, 'minMarkerPerimeterRate': .02,
                         'polygonalApproxAccuracyRate': .04, 'perspectiveRemovePixelPerCell': 12,
                         'perspectiveRemoveIgnoredMarginPerCell': .15}.items():
                setattr(self.parameters, k, v)
        self.detector = a.ArucoDetector(self.dictionary, self.parameters) if hasattr(a, 'ArucoDetector') else None

    def run(self, gray):
        if self.detector is not None:
            return self.detector.detectMarkers(gray)
        return cv2.aruco.detectMarkers(gray, self.dictionary, parameters=self.parameters)

ROS_HELPER_SOURCE = r'''
#!/usr/bin/env python2
"""Read-only ROS Melodic subscriber: raw Image -> latest frame, no cv_bridge.
Separate SHM file from older SmartFleet lane-follower bridge. No motor APIs.
"""
from __future__ import print_function
import argparse
import fcntl
import mmap
import os
import sys
import struct
import threading
import time

MAGIC = b'SFARU001'
GLOBAL = struct.Struct('<8sII')
HEADER = struct.Struct('<QddIIIII')
DATA_OFFSET = 128
ENC = {'bgr8': (1, 3), 'rgb8': (2, 3), 'mono8': (3, 1),
       'rgba8': (4, 4), 'bgra8': (5, 4), '8uc1': (3, 1)}


class RawWriter(object):
    def __init__(self, path, capacity):
        self.path, self.capacity, self.seq = path, capacity, 0
        self.thread_lock = threading.Lock()
        self.owner = open(path + '.lock', 'a+b')
        try:
            fcntl.flock(self.owner, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except IOError:
            self.owner.close()
            raise RuntimeError('A bridge already owns ' + path + '; stop it first.')
        tmp = path + '.new.' + str(os.getpid())
        self.file = open(tmp, 'w+b')
        self.file.truncate(DATA_OFFSET + capacity)
        self.mem = mmap.mmap(self.file.fileno(), 0, access=mmap.ACCESS_WRITE)
        self.mem[:GLOBAL.size] = GLOBAL.pack(MAGIC, capacity, DATA_OFFSET)
        os.rename(tmp, path)

    def publish(self, raw, width, height, step, code, received, captured):
        needed = step * height
        if needed <= 0 or needed > self.capacity or len(raw) < needed:
            raise ValueError('Invalid raw frame size {}; capacity {}'.format(needed, self.capacity))
        with self.thread_lock:
            try:
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except IOError:
                return False  # Never queue behind a reader; the next frame supersedes this one.
            try:
                self.mem[DATA_OFFSET:DATA_OFFSET + needed] = raw[:needed]
                self.seq += 1
                self.mem[GLOBAL.size:GLOBAL.size + HEADER.size] = HEADER.pack(
                    self.seq, received, captured, width, height, step, code, needed)
            finally:
                fcntl.flock(self.file, fcntl.LOCK_UN)
        return True

    def close(self):
        self.mem.close()
        self.file.close()
        self.owner.close()


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--topic', default='/csi_cam_0/image_raw')
    p.add_argument('--shm', default='/dev/shm/smartfleet_aruco_range.bin')
    p.add_argument('--parent-pid', type=int, required=True)
    p.add_argument('--max-mb', type=int, default=32)
    a = p.parse_args()
    if not 1 <= a.max_mb <= 64:
        p.error('max-mb 1..64')
    if sys.version_info[:2] != (2, 7):
        raise RuntimeError('ROS helper must run on Python 2.7, got ' + sys.version)
    print('[ROS HELPER] Python ' + sys.version.split()[0] + ' | ' + sys.executable)
    import rospy
    from sensor_msgs.msg import Image
    rospy.init_node('smartfleet_aruco_raw_range', anonymous=True)
    writer = RawWriter(a.shm, a.max_mb * 1024 * 1024)
    stats = {'report': 0., 'drops': 0, 'geometry': None, 'closing': False}
    callback_lock = threading.Lock()

    def callback(msg):
        # Record callback wall time; ROS header may additionally represent capture time.
        received = time.time()
        with callback_lock:
            if stats['closing']:
                return
            # The actual camera already runs at 20 FPS. No second rate gate.
            encoding = str(msg.encoding).lower()
            try:
                code, channels = ENC[encoding]
                w, h, step = int(msg.width), int(msg.height), int(msg.step)
                if not (16 <= w <= 8192 and 16 <= h <= 8192 and step >= w * channels):
                    raise ValueError('Bad image geometry')
                raw = msg.data
                if not isinstance(raw, bytes):
                    raw = raw.tostring() if hasattr(raw, 'tostring') else bytes(bytearray(raw))
                captured = float(msg.header.stamp.to_sec())
                # Unknown or incompatible clock: explicitly flag, never invent capture time.
                if not captured > 0 or not -0.2 <= received - captured <= 60.0:
                    captured = 0.0
                ok = writer.publish(raw, w, h, step, code, received, captured)
                if not ok:
                    stats['drops'] += 1
                geom = (w, h, step, encoding)
                if geom != stats['geometry'] or received - stats['report'] >= 5.0:
                    print('[RAW ROS] {}x{} {} step={} seq={} lock_drops={} header_age_ms={}'.format(
                        w, h, encoding, step, writer.seq, stats['drops'],
                        round((received - captured) * 1000, 1) if captured else 'unknown'))
                    stats['geometry'], stats['report'] = geom, received
            except (ValueError, KeyError, TypeError, OverflowError, IOError) as exc:
                rospy.logwarn_throttle(3.0, 'ArUco raw bridge: {} (encoding={})'.format(exc, encoding))
    def monitor_parent():
        # A hard Jupyter kernel restart bypasses its finally block.
        while not rospy.is_shutdown():
            if os.getppid() != a.parent_pid:
                rospy.signal_shutdown('Notebook parent exited')
                return
            time.sleep(0.5)

    watcher = threading.Thread(target=monitor_parent)
    watcher.daemon = True
    watcher.start()
    sub = None
    try:
        sub = rospy.Subscriber(a.topic, Image, callback, queue_size=1,
                               buff_size=max(16 * 1024 * 1024, writer.capacity + 65536), tcp_nodelay=True)
        print('[RAW ROS] {} -> {} | native pixels, no HTTP, no resize, no motor commands'.format(a.topic, a.shm))
        rospy.spin()
    finally:
        if sub is not None:
            sub.unregister()
        with callback_lock:
            stats['closing'] = True
            writer.close()


if __name__ == '__main__':
    main()

'''

class NotebookBridge(object):
    def __init__(self, directory):
        self.directory = Path(directory)
        self.path = self.directory / 'raw.bin'
        self.logpath = self.directory / 'bridge.log'
        self.script = self.directory / 'ros_helper.py'
        self.proc = None
        self.log = None
        self.returncode = None

    def start(self):
        self.script.write_text(ROS_HELPER_SOURCE)
        self.log = self.logpath.open('wb')
        env = os.environ.copy()
        # Do not inherit a Python 3 notebook's Python library search paths.
        for key in ('PYTHONPATH', 'PYTHONHOME', 'PYTHONSTARTUP'):
            env.pop(key, None)
        shell = ('source /opt/ros/melodic/setup.bash || exit 11; '
                 'source "$HOME/catkin_ws/devel/setup.bash" || exit 12; '
                 'exec python2 -u "$@"')
        self.proc = subprocess.Popen(
            ['bash', '-c', shell, 'smartfleet-ros-helper', str(self.script),
             '--topic', ROS_TOPIC, '--shm', str(self.path), '--parent-pid', str(os.getpid())],
            env=env, stdout=self.log, stderr=subprocess.STDOUT, start_new_session=True)
        print('[BRIDGE] Python2 ROS subscriber started (PID {}).'.format(self.proc.pid), flush=True)

    def check(self):
        if self.proc is None:
            raise RuntimeError('ROS helper not started')
        code = self.proc.poll()
        if code is not None:
            self.returncode = code
            raise RuntimeError('Python2 ROS helper exited (code {}). See bridge.log.'.format(code))

    def close(self):
        proc = self.proc
        try:
            if proc is not None and proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    proc.wait(timeout=4)
                except subprocess.TimeoutExpired:
                    try:
                        os.killpg(proc.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                    proc.wait(timeout=3)
            if proc is not None:
                self.returncode = proc.poll()
        finally:
            if self.log is not None:
                self.log.close()
                self.log = None
        print('[BRIDGE] Stopped owned helper only; camera ROS node unchanged.', flush=True)
ROS_TOPIC="/csi_cam_0/image_raw"
