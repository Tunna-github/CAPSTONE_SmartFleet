#!/usr/bin/env python3
"""Install guarded Webots intersection pilot for SmartFleet CAPSTONE.\nV2: compatible with both camera-arrival and RETURN_HOME controller variants.

Patches:
- QrDemoRouteService.cs: mark each Backend waypoint with isIntersection.
- MqttNavigationWorker.cs: add correlated, duplicate-safe navigation ACKs.
- Webots_Full_Mission_Controller/my_controller_testing_fleet.py
- optionally the actual Webots world controller with --webots.

This is a WEBOTS simulation protocol, not a certified physical collision system.
No SQL, DB migration, FE changes, Git action or credentials. Backups are made in
an OS temporary directory. Run --dry-run first. See included README.
"""
from __future__ import annotations

import argparse
import datetime as dt
import re
import shutil
import sys
import tempfile
from pathlib import Path

VERSION = 'SMARTFLEET_INTERSECTION_PILOT_V2'
REPO_WEBOTS = Path('Webots_Full_Mission_Controller/my_controller_testing_fleet.py')
ROUTE = Path('SmartFleetBE/Services/QrDemoRouteService.cs')
NAV_WORKER = Path('SmartFleetBE/Services/MQTT/MqttNavigationWorker.cs')
HERE = Path(__file__).resolve().parent


def subonce(src: str, old: str, replacement: str, name: str) -> str:
    count = src.count(old)
    if count != 1:
        raise ValueError(f'{name}: expected one anchor, got {count}; no files changed')
    return src.replace(old, replacement, 1)


def patch_route(s: str) -> str:
    if 'SMARTFLEET_INTERSECTION_WAYPOINT_V1' in s:
        return s
    s = subonce(s,
        'public string Stage { get; init; } = "TO_PICKUP";',
        'public string Stage { get; init; } = "TO_PICKUP";\n'
        '        // SMARTFLEET_INTERSECTION_WAYPOINT_V1: configured Backend safety flag.\n'
        '        public bool IsIntersection { get; init; }', 'QR waypoint property')
    old = 'Col = nodes[id].Col,'
    if s.count(old) not in (1, 2):
        raise ValueError(f'QR waypoint initializers: expected 1..2, got {s.count(old)}')
    s = s.replace(old,
        'Col = nodes[id].Col,\n'
        '                IsIntersection = map.Cells[nodes[id].Row][nodes[id].Col].IsIntersection,')
    return s


def patch_worker(s: str) -> str:
    if 'SMARTFLEET_NAV_ACK_V1' in s:
        return s
    s = subonce(s,
        'private readonly SemaphoreSlim _messageGate = new(1, 1);',
        'private readonly SemaphoreSlim _messageGate = new(1, 1);\n'
        '    // SMARTFLEET_NAV_ACK_V1: dedupe accepted requests on QoS1 resend.\n'
        '    private readonly Dictionary<string, (string Robot, string Kind)> _ackCache = new(StringComparer.Ordinal);\n'
        '    private readonly Queue<string> _ackOrder = new();', 'navigation cache')
    s = subonce(s, 'var payload = args.ApplicationMessage.ConvertPayloadToString();',
        'var payload = args.ApplicationMessage.ConvertPayloadToString();\n'
        '            // Correlation ID is optional for older clients; pilot clients always send it.\n'
        '            using var requestDocument = JsonDocument.Parse(payload);\n'
        '            string? eventId = null;\n'
        '            if (requestDocument.RootElement.ValueKind == JsonValueKind.Object &&\n'
        '                requestDocument.RootElement.TryGetProperty("eventId", out var idProperty) &&\n'
        '                idProperty.ValueKind == JsonValueKind.String)\n'
        '                eventId = idProperty.GetString();\n'
        '            if (eventId is { Length: > 128 }) return;\n'
        '            var kind = parts[3] == "scan" ? "SCAN" : "";', 'nav request ID')
    s = subonce(s,
        'if (evt == null && scan == null) return;',
        'if (evt == null && scan == null) return;\n'
        '            if (scan == null) kind = evt!.Event.Trim().ToUpperInvariant();', 'nav request kind')
    s = subonce(s, 'if (robot == null) return;',
        'if (robot == null) return;\n'
        '                if (eventId != null && _ackCache.TryGetValue(eventId, out var old))\n'
        '                {\n'
        '                    bool sameRequest = string.Equals(old.Robot, robot.RobotCode, StringComparison.OrdinalIgnoreCase)\n'
        '                        && string.Equals(old.Kind, kind, StringComparison.Ordinal);\n'
        '                    await PublishPilotAck(robot.RobotCode, kind, eventId, sameRequest,\n'
        '                        sameRequest ? "Already applied" : "Event ID reused for different robot/event", ct);\n'
        '                    return;\n'
        '                }', 'deduped navigation event')
    s = subonce(s,
        '// Rejected navigation is always a local HOLD/STOP case for the robot.\n                    return;',
        '// Rejected navigation is always a local HOLD/STOP case for the robot.\n'
        '                    await PublishPilotAck(robot.RobotCode, kind, eventId, false, result.Error, ct);\n'
        '                    return;', 'rejected navigation ack')
    s = subonce(s, 'await NavigationMqttDispatch.PublishAsync(publisher, result.Directives, ct);',
        'await NavigationMqttDispatch.PublishAsync(publisher, result.Directives, ct);\n'
        '                if (eventId != null)\n'
        '                {\n'
        '                    _ackCache[eventId] = (robot.RobotCode, kind);\n'
        '                    _ackOrder.Enqueue(eventId);\n'
        '                    while (_ackOrder.Count > 4096)\n'
        '                        _ackCache.Remove(_ackOrder.Dequeue());\n'
        '                }\n'
        '                await PublishPilotAck(robot.RobotCode, kind, eventId, true, "Applied", ct);',
        'accepted navigation ack')
    method = '''
    // SMARTFLEET_NAV_ACK_V1: only a Backend-applied navigation state is ACKed.
    private Task PublishPilotAck(string robotCode, string kind, string? eventId,
        bool accepted, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return Task.CompletedTask;
        return publisher.PublishAsync($"smartfleet/robot/{robotCode}/navigation-ack",
            JsonSerializer.Serialize(new { robotCode, kind, eventId, accepted, reason }, Options), ct);
    }
'''
    if not s.endswith('}\n'):
        raise ValueError('MqttNavigationWorker class end changed')
    return s[:-2] + method + '}\n'


INTERSECTION_CODE = '''# SF_INTERSECTION_PILOT_V1: fail-closed Webots intersection reservation.
# This pilot uses simulated CLEAR sensor messages only when explicitly enabled.
# Real physical robots MUST use actual obstacle sensing and safety-rated controls.

def sf_timestamp(raw):
    if not isinstance(raw, str):
        return None
    try:
        value = sf_datetime.datetime.fromisoformat(raw.replace('Z', '+00:00'))
        if value.tzinfo is None:
            return None
        return value.astimezone(sf_datetime.timezone.utc)
    except (ValueError, TypeError):
        return None


def sf_accept_nav_directive(data):
    global sf_nav_epoch, sf_nav_sequence, sf_nav_directive, sf_nav_notice
    if not INTERSECTION_PILOT or data.get('robotCode') != ROBOT_CODE:
        return
    epoch, seq = data.get('epoch'), data.get('sequence')
    expiry = sf_timestamp(data.get('expiresAtUtc'))
    if not isinstance(epoch, str) or not epoch or type(seq) is not int or expiry is None:
        sf_nav_directive = None
        stop_robot()
        return
    if sf_nav_epoch is not None and sf_nav_epoch != epoch:
        stop_robot()
        sf_nav_directive = None
        # Coordinator restart may have lost transient state; never resume motion
        # using a GO from the new epoch without manual recovery.
        if mission is not None:
            print('[TRAFFIC {}] BACKEND EPOCH CHANGED: halting, check intersection lock'.format(ROBOT_CODE))
            fail_mission('Navigation Backend restarted during mission; manual recovery required',
                         notify_backend=False)
            return
        sf_nav_sequence = -1
    if sf_nav_epoch == epoch and seq <= sf_nav_sequence:
        return
    sf_nav_epoch, sf_nav_sequence = epoch, seq
    if expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        sf_nav_directive = None
        stop_robot()
        return
    if data.get('mode') not in ('STOP', 'SLOW', 'GO'):
        sf_nav_directive = None
        stop_robot()
        return
    sf_nav_directive = dict(data)
    sf_nav_directive['_expiry'] = expiry
    notice = (data.get('mode'), data.get('intersectionArUcoId'),
              data.get('intersectionGranted'), data.get('reason'))
    if notice != sf_nav_notice:
        print('[TRAFFIC {}] NAV {} intersection={} granted={} reason={}'.format(
            ROBOT_CODE, *notice))
        sf_nav_notice = notice
    if data.get('mode') == 'STOP':
        stop_robot()


def sf_accept_nav_ack(data):
    if not INTERSECTION_PILOT or sf_nav_request is None:
        return
    if data.get('robotCode') != ROBOT_CODE or data.get('eventId') != sf_nav_request['eventId']:
        return
    if data.get('kind') != sf_nav_request['kind']:
        return
    sf_nav_request['accepted'] = data.get('accepted') is True
    sf_nav_request['reason'] = str(data.get('reason', ''))


def sf_validate_intersection_waypoints(waypoints):
    if not isinstance(waypoints, list) or len(waypoints) < 2:
        raise ValueError('Traffic pilot requires at least two route checkpoints')
    for w in waypoints:
        if type(w.get('isIntersection')) is not bool:
            raise ValueError('Backend route missing isIntersection; install Backend pilot patch first')
    if waypoints[0]['isIntersection'] or waypoints[-1]['isIntersection']:
        raise ValueError('Traffic pilot route must start and finish OUTSIDE intersections')
    if any(a['isIntersection'] and b['isIntersection'] for a, b in zip(waypoints, waypoints[1:])):
        raise ValueError('Adjacent intersections unsupported in safe pilot')


def sf_nav_send(kind, data, payload, intersection_id=None):
    global sf_nav_request
    if sf_nav_request is not None:
        raise ValueError('Navigation request still pending')
    event_id = str(uuid.uuid4())
    payload = dict(payload)
    payload['eventId'] = event_id
    topic = SF_SCAN_TOPIC if kind == 'SCAN' else SF_NAV_EVENT_TOPIC
    sf_nav_request = {
        'kind': kind, 'eventId': event_id, 'topic': topic, 'payload': payload,
        'intersectionId': intersection_id, 'accepted': None, 'reason': '',
        'deadline': time.monotonic() + SF_NAV_ACK_TIMEOUT,
        'retry_at': time.monotonic() + SF_NAV_ACK_RETRY
    }
    data['phase'] = 'NAV_WAIT_ACK'
    stop_robot()
    if not publish(topic, payload):
        print('[TRAFFIC {}] Waiting for MQTT to send {}'.format(ROBOT_CODE, kind))


def sf_begin_segment(data):
    """Stop at predecessor checkpoint and register before any next movement."""
    if not INTERSECTION_PILOT:
        return sf_start_physical_segment(data)
    i = data['next_index']
    if i < 1 or i >= len(data['waypoints']):
        raise ValueError('Invalid nav waypoint index')
    prev, target = data['waypoints'][i - 1], data['waypoints'][i]
    if current_marker != prev['arucoId']:
        raise ValueError('Camera QR does not match predecessor stop checkpoint')
    if sf_active_intersection is not None:
        if prev['arucoId'] != sf_active_intersection or target['isIntersection']:
            raise ValueError('Unexpected active intersection reservation on next segment')
        data['phase'] = 'NAV_WAIT_GRANT'
        data['sf_hold_until'] = time.monotonic() + SF_TEST_HOLD_SECONDS
        if SF_TEST_HOLD_SECONDS:
            print('[TRAFFIC {}] TEST HOLD for {:.1f}s inside QR{} (Webots-only)'.format(
                ROBOT_CODE, SF_TEST_HOLD_SECONDS, sf_active_intersection))
        print('[TRAFFIC {}] Keep QR{} reserved while driving toward exit QR{}'.format(
            ROBOT_CODE, sf_active_intersection, target['arucoId']))
        stop_robot()
        return
    if target['isIntersection']:
        kind = 'APPROACH'
        payload = {'event': 'APPROACH', 'intersectionArUcoId': target['arucoId'],
                   'currentArUcoId': current_marker}
        print('[TRAFFIC {}] APPROACH QR{} from QR{}; stopped for Backend grant'.format(
            ROBOT_CODE, target['arucoId'], current_marker))
        sf_nav_send(kind, data, payload, target['arucoId'])
    else:
        print('[TRAFFIC {}] Register corridor QR{} -> QR{}'.format(
            ROBOT_CODE, current_marker, target['arucoId']))
        sf_nav_send('SCAN', data, {'currentArUcoId': current_marker,
                                   'nextArUcoId': target['arucoId']})


def sf_move_permission(data):
    """Fresh Backend directive is mandatory for *every* moving segment."""
    directive = sf_nav_directive
    if not connected or directive is None:
        return False
    if directive.get('mode') not in ('GO', 'SLOW'):
        return False
    expiry = directive.get('_expiry')
    if expiry is None or expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        return False
    try:
        speed = float(directive.get('maxLinearSpeed', 0))
        if not 0 < speed <= 5:
            return False
    except (ValueError, TypeError):
        return False
    i = data.get('next_index', 0)
    if i < 1 or i >= len(data['waypoints']):
        return False
    target = data['waypoints'][i]
    protected = target['arucoId'] if target['isIntersection'] else sf_active_intersection
    if protected is not None:
        if (directive.get('intersectionArUcoId') != protected or
                directive.get('intersectionGranted') is not True or
                directive.get('mode') != 'GO'):
            return False
    else:
        if directive.get('intersectionArUcoId') is not None:
            return False
    return True


def sf_tick_nav_gate(data):
    global sf_nav_request, sf_active_intersection, sf_last_hold_log
    stop_robot()
    if data['phase'] == 'NAV_WAIT_ACK':
        req = sf_nav_request
        if req is None:
            fail_mission('Missing navigation request state')
            return
        if req['accepted'] is False:
            fail_mission('Backend rejected navigation {}: {}'.format(req['kind'], req['reason']))
            return
        if req['accepted'] is None:
            now = time.monotonic()
            if now >= req['deadline']:
                fail_mission('No Backend ACK for navigation {}'.format(req['kind']))
                return
            if connected and now >= req['retry_at']:
                publish(req['topic'], req['payload'])
                req['retry_at'] = now + SF_NAV_ACK_RETRY
            return
        kind, protected = req['kind'], req['intersectionId']
        sf_nav_request = None
        if kind in ('SCAN', 'APPROACH'):
            data['phase'] = 'NAV_WAIT_GRANT'
            return
        if kind == 'ENTER':
            sf_active_intersection = protected
            print('[TRAFFIC {}] ENTER QR{} acknowledged; reservation held'.format(
                ROBOT_CODE, protected))
            sf_start_physical_segment(data)
            return
        if kind == 'EXIT':
            print('[TRAFFIC {}] EXIT QR{} acknowledged at QR{}; lock released'.format(
                ROBOT_CODE, sf_active_intersection, current_marker))
            sf_active_intersection = None
            if data.get('home_command_id'):
                data['next_index'] += 1
                if data['next_index'] >= len(data['waypoints']):
                    finish_home(data)
                else:
                    sf_begin_segment(data)
            else:
                send_progress(data, data['next_index'])
                data['phase'] = 'WAIT_WAYPOINT_ACK'
            return
        fail_mission('Unexpected navigation ack kind: ' + kind)
        return
    if data['phase'] == 'NAV_WAIT_GRANT':
        if time.monotonic() < data.get('sf_hold_until', 0):
            return
        if not sf_move_permission(data):
            now = time.monotonic()
            if now - sf_last_hold_log >= 5:
                target = data['waypoints'][data['next_index']]['arucoId']
                print('[TRAFFIC {}] HOLD at QR{}: waiting valid GO for QR{}'.format(
                    ROBOT_CODE, current_marker, target))
                sf_last_hold_log = now
            return
        next_wp = data['waypoints'][data['next_index']]
        if next_wp['isIntersection']:
            intersection_id = next_wp['arucoId']
            print('[TRAFFIC {}] GRANTED QR{}; confirm protected entry'.format(
                ROBOT_CODE, intersection_id))
            sf_nav_send('ENTER', data, {'event': 'ENTER',
                                       'intersectionArUcoId': intersection_id}, intersection_id)
        else:
            sf_start_physical_segment(data)
        return
    fail_mission('Invalid traffic gate phase ' + str(data['phase']))


def sf_tick_lidar():
    """Simulated traffic heartbeat, NOT a physical obstacle detector."""
    global sf_last_lidar_at
    if not INTERSECTION_PILOT or not SF_FAKE_CLEAR or not connected:
        return
    now = time.monotonic()
    if now - sf_last_lidar_at < SF_LIDAR_INTERVAL:
        return
    sf_last_lidar_at = now
    publish(SF_LIDAR_TOPIC, {'status': 'CLEAR', 'distanceCm': None,
                             'simulated': True, 'source': 'WEBOTS_PILOT_ONLY'}, qos=1)


def sf_wheel_scale():
    """Apply NAV_CONTROL's speed as a conservative *relative* wheel cap.

    BE maxLinearSpeed is a ROS-style command, not calibrated wheel rad/s.
    The 0.18 reference only maps demo speeds; it is not a real m/s claim.
    """
    if not INTERSECTION_PILOT:
        return 1.0
    if sf_nav_directive is None or sf_nav_directive.get('mode') == 'STOP':
        return 0.0
    expiry = sf_nav_directive.get('_expiry')
    if expiry is None or expiry <= sf_datetime.datetime.now(sf_datetime.timezone.utc):
        return 0.0
    try:
        cap = float(sf_nav_directive.get('maxLinearSpeed', 0))
        return max(0.0, min(1.0, cap / 0.18))
    except (ValueError, TypeError):
        return 0.0
'''


def patch_webots(s: str) -> str:
    if '# SF_INTERSECTION_PILOT_V1' in s:
        return s
    if '_sf_controller_arg(' not in s or 'SMARTFLEET_MULTI_ROBOT_ARGS_V1' not in s:
        raise ValueError('Webots file lacks per-robot controllerArgs patch; apply previous Multi-Webots upgrade first.')
    if 'def reached_marker(data, detected_id):' not in s or 'def tick_mission():' not in s:
        raise ValueError('Webots camera mission functions not found')
    s = subonce(s, 'import uuid\n', 'import uuid\nimport datetime as sf_datetime\n', 'datetime import')
    heading_line = 'FIRST_RUN_HEADING = _sf_controller_arg("start-heading", "S").upper()'
    # The first-run heading validation follows this assignment. Configuration can be
    # placed just BEFORE the controller's time constants rather than inside if.
    s = subonce(s, 'READY_HEARTBEAT_SECONDS = 10.0',
        '''# WEBOTS ONLY. Never enable fake sensor CLEAR on a real JetRacer.
INTERSECTION_PILOT = _sf_controller_arg("intersection-pilot", "false").lower() in ("true", "1", "yes")
SF_FAKE_CLEAR = _sf_controller_arg("sim-lidar", "false").lower() in ("true", "1", "yes")
SF_NAV_ACK_TIMEOUT = 15.0
SF_NAV_ACK_RETRY = 1.5
SF_LIDAR_INTERVAL = 0.8
SF_TEST_HOLD_SECONDS = max(0.0, min(30.0, float(_sf_controller_arg("traffic-test-hold", "0"))))
READY_HEARTBEAT_SECONDS = 10.0''', 'traffic config')
    if heading_line not in s:
        raise ValueError('Per-robot start-heading config missing')
    s = subonce(s, 'TELEMETRY_TOPIC = "smartfleet/robot/{}/telemetry".format(ROBOT_CODE)',
        '''TELEMETRY_TOPIC = "smartfleet/robot/{}/telemetry".format(ROBOT_CODE)
SF_SCAN_TOPIC = "smartfleet/robot/{}/scan".format(ROBOT_CODE)
SF_NAV_EVENT_TOPIC = "smartfleet/robot/{}/navigation".format(ROBOT_CODE)
SF_NAV_ACK_TOPIC = "smartfleet/robot/{}/navigation-ack".format(ROBOT_CODE)
SF_NAV_CONTROL_TOPIC = "smartfleet/robot/{}/nav-control".format(ROBOT_CODE)
SF_LIDAR_TOPIC = "smartfleet/robot/{}/lidar".format(ROBOT_CODE)''', 'Webots traffic topics')
    s = subonce(s, 'last_bootstrap_warning_at = 0.0',
        '''last_bootstrap_warning_at = 0.0
sf_nav_epoch = None
sf_nav_sequence = -1
sf_nav_directive = None
sf_nav_notice = None
sf_nav_request = None
sf_active_intersection = None
sf_last_lidar_at = 0.0
sf_last_hold_log = 0.0
sf_reconnected_with_mission = False''', 'Webots gate globals')
    s = subonce(s,
        '    v_l = max(-ROBOT_MAX_VEL, min(ROBOT_MAX_VEL, v_left))',
        '''    if INTERSECTION_PILOT and mission is not None and mission.get("phase") == "MOVING":
        factor = sf_wheel_scale()
        v_left *= factor
        v_right *= factor
    v_l = max(-ROBOT_MAX_VEL, min(ROBOT_MAX_VEL, v_left))''',
        'respect STOP/SLOW wheel cap')
    s = subonce(s, 'def on_disconnect(cli, userdata, disconnect_flags, reason_code, properties=None):\n    global connected',
        'def on_disconnect(cli, userdata, disconnect_flags, reason_code, properties=None):\n'
        '    global sf_reconnected_with_mission\n'
        '    if INTERSECTION_PILOT and mission is not None:\n'
        '        sf_reconnected_with_mission = True\n'
        '    global connected', 'fail closed on reconnect')
    s = subonce(s, '(QR_PROGRESS_ACK_TOPIC, 1)])',
        '(QR_PROGRESS_ACK_TOPIC, 1), (SF_NAV_CONTROL_TOPIC, 1),\n'
        '                       (SF_NAV_ACK_TOPIC, 1)])', 'Webots subscribe nav')
    s = subonce(s,
        'elif topic == QR_PROGRESS_ACK_TOPIC and ready_event_id is not None and \\',
        '''elif topic == SF_NAV_CONTROL_TOPIC:
            sf_accept_nav_directive(data)
        elif topic == SF_NAV_ACK_TOPIC:
            sf_accept_nav_ack(data)
        elif topic == QR_PROGRESS_ACK_TOPIC and ready_event_id is not None and \\''', 'Webots incoming nav branch')
    s = subonce(s, 'def begin_segment(data):',
        INTERSECTION_CODE + '\n\ndef begin_segment(data):\n'
        '    return sf_begin_segment(data)\n\n\ndef sf_start_physical_segment(data):',
        'Webots traffic helper insertion')
    # COMPAT V2: older camera-arrival controller has no RETURN_HOME branch.
    # Confirmed waypoint arrival always passes through this single marker;
    # insert EXIT before progress delivery, after any optional HOME branch.
    s = subonce(s,
        '    send_progress(data, i)\n    data["phase"] = "WAIT_WAYPOINT_ACK"',
        '''    if INTERSECTION_PILOT and sf_active_intersection is not None and i > 0 and \
            data["waypoints"][i - 1]["arucoId"] == sf_active_intersection:
        if data["waypoints"][i].get("isIntersection"):
            raise ValueError("Cannot exit directly into a second intersection")
        print("[TRAFFIC {}] QR{} camera-confirmed beyond intersection QR{}; request EXIT".format(
            ROBOT_CODE, current_marker, sf_active_intersection))
        sf_nav_send("EXIT", data, {"event": "EXIT", "intersectionArUcoId": sf_active_intersection,
                                   "currentArUcoId": current_marker}, sf_active_intersection)
        return
    send_progress(data, i)
    data["phase"] = "WAIT_WAYPOINT_ACK"''',
        'Webots exit after camera')
    s = subonce(s,
        '    data = mission\n    if not connected:',
        '    data = mission\n'
        '    if INTERSECTION_PILOT and sf_reconnected_with_mission:\n'
        '        stop_robot()\n'
        '        fail_mission("MQTT connection lost during reserved navigation; manual recovery required",\n'
        '                     notify_backend=False)\n'
        '        return\n'
        '    if not connected:', 'MQTT interruption fail closed')
    s = subonce(s,
        '    if pending is not None:\n        stop_robot()\n        return\n    if data["phase"] != "MOVING":',
        '''    if pending is not None:
        stop_robot()
        return
    if INTERSECTION_PILOT:
        if data["phase"] in ("NAV_WAIT_ACK", "NAV_WAIT_GRANT"):
            sf_tick_nav_gate(data)
            return
        if data["phase"] == "MOVING" and not sf_move_permission(data):
            stop_robot()
            return
    if data["phase"] != "MOVING":''', 'movement interlock')
    s = subonce(s,
        '    if ids[0] != data["startArUcoId"] or ids[-1] != data["deliveryArUcoId"]:',
        '''    if INTERSECTION_PILOT:
        sf_validate_intersection_waypoints(waypoints)
    if ids[0] != data["startArUcoId"] or ids[-1] != data["deliveryArUcoId"]:''',
        'Webots route safety validation')
    s = subonce(s, 'def fail_mission(reason, notify_backend=True):',
        'def fail_mission(reason, notify_backend=True):\n    global sf_nav_request, sf_reconnected_with_mission\n    sf_nav_request = None\n    sf_reconnected_with_mission = False',
        'clear nav requests after mission failure')
    s = subonce(s, '    validate_command(data)\n    mission = dict(data)',
        '''    validate_command(data)
    if INTERSECTION_PILOT and sf_active_intersection is not None:
        raise ValueError("Previous intersection reservation still active; investigate/clear physically")
    mission = dict(data)''', 'reject new task with held lock')
    # COMPAT V2: Return-Home safety controls were added after the camera
    # arrival-fix release; the actual Webots world may use that older release.
    # Patch this additional safety path only if it exists. Do NOT invent a
    # RETURN_HOME implementation; the intersection protocol is simulation-only.
    if '        mission = dict(data)\n        safety_generation[task_id] = generation' in s:
        s = subonce(s, '        mission = dict(data)\n        safety_generation[task_id] = generation',
            '''        if INTERSECTION_PILOT:
            try:
                sf_validate_intersection_waypoints(waypoints)
                if sf_active_intersection is not None:
                    raise ValueError("active intersection reservation requires recovery")
            except ValueError as ex:
                print("[SAFETY HOLD] RETURN_HOME not safe under traffic protocol:", ex)
                return
        mission = dict(data)
        safety_generation[task_id] = generation''', 'return-home safety check')
    s = subonce(s, '        tick_telemetry()\n        tick_ready()',
        '        tick_telemetry()\n        sf_tick_lidar()\n        tick_ready()', 'pilot simulated LiDAR')
    s = subonce(s,
        'print("[BOOT] MANUAL 0-8 keyboard mission controls are disabled; Q stops the pilot")',
        '''print("[BOOT] MANUAL 0-8 keyboard mission controls are disabled; Q stops the pilot")
if INTERSECTION_PILOT:
    print("[TRAFFIC] INTERSECTION PILOT ON for", ROBOT_CODE,
          "| SIMULATED CLEAR (Webots only) =", SF_FAKE_CLEAR)
    if not SF_FAKE_CLEAR:
        print("[TRAFFIC] Without fresh real sensor data the Backend must keep the robot stopped")''',
        'traffic pilot startup log')
    return s


def read_file(path: Path) -> tuple[str, bytes, str]:
    b = path.read_bytes()
    bom = b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b''
    s = b[len(bom):].decode('utf-8')
    newline = '\r\n' if '\r\n' in s else '\n'
    return s.replace('\r\n', '\n'), bom, newline


def output_bytes(s: str, bom: bytes, newline: str) -> bytes:
    return bom + s.replace('\n', newline).encode('utf-8')


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--repo', required=True, type=Path)
    ap.add_argument('--webots', type=Path,
                    help='Actual Webots controller file outside repository (strongly recommended)')
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args()
    repo = args.repo.expanduser().resolve()
    targets = [(repo / ROUTE, patch_route), (repo / NAV_WORKER, patch_worker),
               (repo / REPO_WEBOTS, patch_webots)]
    if args.webots:
        path = args.webots.expanduser().resolve()
        if path != (repo / REPO_WEBOTS).resolve():
            targets.append((path, patch_webots))
    plans = []
    try:
        for path, operation in targets:
            if not path.is_file():
                raise FileNotFoundError('Missing required file: ' + str(path))
            original, bom, newline = read_file(path)
            patched = operation(original)
            if path.suffix == '.py':
                compile(patched, str(path), 'exec')
            plans.append((path, patched, bom, newline, path.read_bytes()))
    except (OSError, ValueError, SyntaxError) as exc:
        print('ABORT: ' + str(exc))
        print('No changes written. Verify current code version / apply Multi-Webots setup first.')
        return 2
    changed = [p for p in plans if output_bytes(p[1], p[2], p[3]) != p[4]]
    print(f'Validated {len(plans)} paths; {len(changed)} need updates.')
    for path, *_ in plans:
        print(('CHANGE' if any(p[0] == path for p in changed) else 'UNCHANGED'), path)
    if args.dry_run:
        print('DRY RUN: no files modified')
        return 0
    if not changed:
        print('ALREADY INSTALLED: no changes necessary')
        return 0
    backup_folder = Path(tempfile.mkdtemp(prefix='smartfleet_intersection_backup_' +
        dt.datetime.now().strftime('%Y%m%d_%H%M%S') + '_'))
    try:
        for i, (path, _, _, _, original) in enumerate(changed):
            backup = backup_folder / (str(i) + '_' + path.name)
            backup.write_bytes(original)
        for path, patched, bom, newline, _ in changed:
            path.write_bytes(output_bytes(patched, bom, newline))
    except OSError as err:
        print('INSTALL FAILED:', err, ' | Backup folder:', backup_folder)
        return 3
    print('APPLIED. BACKUPS:', backup_folder)
    print('Build BE; restart BE with Navigation__Enabled=true; reset Webots.')
    print('Add --intersection-pilot=true --sim-lidar=true to BOTH robot controllerArgs.')
    print('Mark QR1 as intersection on Warehouse Maps before starting tasks.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
