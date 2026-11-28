"""Python 3.6-compatible physical adapter. No ROS/MQTT imports or side effects."""
import datetime
import math
import re
from line_vision import is_line_demo


def wrap(angle):
    return (angle+180) % 360-180


def utc(raw):
    if not isinstance(raw, str):
        raise ValueError('Missing backend UTC timestamp')
    match = re.fullmatch(r'(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?(Z|[+-]\d{2}:\d{2})', raw)
    if not match:
        raise ValueError('Invalid backend UTC timestamp')
    date = datetime.datetime.strptime(match[1], '%Y-%m-%dT%H:%M:%S')
    micros = int((match[2] or '').ljust(6, '0')[:6])
    offset = 0 if match[3]=='Z' else (1 if match[3][0]=='+' else -1)*(int(match[3][1:3])*60+int(match[3][4:6]))
    return (date.replace(microsecond=micros)-datetime.timedelta(minutes=offset)-datetime.datetime(1970,1,1)).total_seconds()


def direction(a, b):
    dr, dc = b['row']-a['row'], b['col']-a['col']
    if dr == 0 and dc:
        return 'E' if dc>0 else 'W'
    if dc == 0 and dr:
        return 'S' if dr>0 else 'N'
    raise ValueError('Route segment is not an axis-aligned corridor')


HEADINGS = {'N':0, 'W':90, 'S':-180, 'E':-90}


def validate_profile(p):
    if is_line_demo(p):
        if p.get('demoEnabled') is not True:
            raise ValueError('Enable the explicit supervised marker/line demo profile')
        if type(p.get('maxSpeedMps')) not in (int,float) or not 0<p['maxSpeedMps']<=.18:
            raise ValueError('Line demo speed must be 0..0.18 m/s command units')
        if type(p.get('segmentTimeoutSeconds')) not in (int,float) or not 0<p['segmentTimeoutSeconds']<=45:
            raise ValueError('Line demo requires a bounded segment timeout <=45 seconds')
        if type(p.get('markerConfirmFrames')) is not int or not 2<=p['markerConfirmFrames']<=10:
            raise ValueError('Require 2..10 distinct frames per marker confirmation')
        top,bottom=p.get('lineRoi',[.2,.9])
        if not 0<=top<bottom<=1 or not 0<p.get('maxLineSteerRad',.2)<=.6:
            raise ValueError('Invalid line ROI/steering limits')
        return
    if p.get('commissioned') is not True:
        raise ValueError('Physical layout/camera/encoder commissioning is pending; driving remains disabled')
    for key in ('metersPerGridCell', 'arrivalRadiusM', 'intersectionClearanceM', 'maxSpeedMps', 'segmentTimeoutSeconds','maxVisionGapSeconds','maxOdometryGapM'):
        value = p.get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
            raise ValueError('Measure/configure '+key)
    if p['maxSpeedMps'] > .18 or p['arrivalRadiusM'] >= p['intersectionClearanceM']:
        raise ValueError('Invalid physical safety limits')
    if p['segmentTimeoutSeconds']>=20:
        raise ValueError('Segment timeout must be below the independent 20-second IO watchdog')
    if p.get('encoderScaleValidated') is not True or p.get('camera', {}).get('validated') is not True:
        raise ValueError('Camera installation and encoder scale must be validated')
    markers = p.get('markers', [])
    ids = [m['arucoId'] for m in markers]
    if len(ids)<2 or len(ids)!=len(set(ids)) or len({(m['row'],m['col']) for m in markers})!=len(ids):
        raise ValueError('Duplicate or missing physical checkpoints')
    for marker in markers:
        if (type(marker.get('arucoId')) is not int or not 0<=marker['arucoId']<50 or
            type(marker.get('row')) is not int or not 0<=marker['row']<18 or
            type(marker.get('col')) is not int or not 0<=marker['col']<28 or
            type(marker.get('isIntersection')) is not bool):
            raise ValueError('Invalid physical marker identity/grid metadata')
        if (type(marker.get('markerSizeM')) not in (int,float) or not .005<=marker['markerSizeM']<=2 or
            type(marker.get('markerYawDeg')) not in (int,float) or not -180<=marker['markerYawDeg']<=180):
            raise ValueError('Measure black marker size/orientation; paper size is insufficient')
    by_label={'QR'+str(m['arucoId']):m for m in markers}
    for link in p.get('links',[]):
        if len(link)!=2 or any(x not in by_label for x in link):
            raise ValueError('Unknown corridor checkpoint')
        a,b=(by_label[x] for x in link)
        direction(a,b)


def validate_map(p, m):
    validate_profile(p)
    if is_line_demo(p):
        if m.get('frame')!='MAP_N_W_UP' or m.get('markerDictionary') not in ('DICT_4X4_50','DICT_4X4_250'):
            raise ValueError('Unsupported backend checkpoint topology')
        ids=[x['arucoId'] for x in m.get('markers',[])]
        if len(ids)<2 or len(set(ids))!=len(ids):raise ValueError('Missing/duplicate backend markers')
        return  # Logical topology, not a claim about measured floor dimensions.
    if m.get('frame')!='MAP_N_W_UP' or m.get('markerDictionary')!='DICT_4X4_50':
        raise ValueError('Backend coordinate frame/dictionary differs from physical installation')
    if abs(m.get('metersPerGridCell', 0)-p['metersPerGridCell']) > 1e-6:
        raise ValueError('Physical spacing differs from backend grid scale')
    expected = {x['arucoId']:x for x in p['markers']}
    actual = {x['arucoId']:x for x in m.get('markers', [])}
    if len(actual)!=len(m.get('markers', [])) or set(actual)!=set(expected):
        raise ValueError('Backend markers differ from the physical scene; never reuse a different Webots layout')
    for key, a in expected.items():
        b = actual[key]
        for field in ('row','col','markerYawDeg','markerSizeM','isIntersection'):
            if a.get(field)!=b.get(field):
                raise ValueError('Physical QR{} {} differs from backend'.format(key, field))
    if bool(m.get('directedLinks'))!=bool(p.get('directedLinks')):
        raise ValueError('Lane direction differs')
    edges = lambda links: {tuple(edge) if p.get('directedLinks') else tuple(sorted(edge)) for edge in links}
    if edges(m.get('links', []))!=edges(p.get('links', [])):
        raise ValueError('Backend corridors differ from measured physical layout')


def line_checkpoint_edges(graph):
    """Resolve configured links to IDs; display labels are not physical identities."""
    known = {marker['arucoId'] for marker in graph['markers']}
    if 'checkpointLinks' in graph:
        edges = graph['checkpointLinks']
        if not isinstance(edges, list) or any(
                not isinstance(edge, list) or len(edge)!=2 or
                any(type(ident) is not int or ident not in known for ident in edge) or edge[0]==edge[1]
                for edge in edges):
            raise ValueError('Invalid configured checkpoint links')
        return {tuple(edge) for edge in edges}
    # Older backends export QR labels only, including zero-padded QR06/QR07.
    labels = {marker['label'].casefold(): marker['arucoId']
              for marker in graph['markers'] if isinstance(marker.get('label'), str)}
    def resolve(label):
        if not isinstance(label, str):
            return None
        if label.casefold() in labels:
            return labels[label.casefold()]
        match = re.fullmatch(r'QR(\d+)', label, re.IGNORECASE)
        ident = int(match[1]) if match else None
        return ident if ident in known else None
    result = set()
    for link in graph.get('links', []):
        if not isinstance(link, list) or len(link)!=2:
            continue
        pair = tuple(resolve(label) for label in link)
        if None not in pair and pair[0]!=pair[1]:
            result.add(pair)
    return result


def validate_route(p, m, route, current, heading):
    validate_map(p, m)
    if route.get('command')!='EXECUTE_TASK' or route.get('pattern')!='WAYPOINT_ROUTE' or route.get('verificationRequired') is not True:
        raise ValueError('Only current backend WAYPOINT_ROUTE with transfer verification is supported')
    for key in ('taskId','assignmentId','missionId','mapRevision','pickupWaypointIndex'):
        if type(route.get(key)) is not int or route[key] < (0 if key=='pickupWaypointIndex' else 1):
            raise ValueError('Invalid '+key)
    if route['mapRevision']!=m['mapRevision'] or (not is_line_demo(p) and abs(route.get('metersPerGridCell',0)-p['metersPerGridCell'])>1e-6):
        raise ValueError('Stale route/scale')
    waypoints = route.get('waypoints', [])
    if not 2<=len(waypoints)<=100:
        raise ValueError('Invalid waypoint count')
    by_id = {x['arucoId']:x for x in (m['markers'] if is_line_demo(p) else p['markers'])}
    ids = [x['arucoId'] for x in waypoints]
    pi = route['pickupWaypointIndex']
    if not 0<=pi<len(ids)-1 or ids[0]!=current or ids[0]!=route.get('startArUcoId') or ids[pi]!=route.get('pickupArUcoId') or ids[-1]!=route.get('deliveryArUcoId'):
        raise ValueError('Station/route/current camera checkpoint mismatch')
    if route.get('routeToPickup')!=ids[:pi+1] or route.get('routeToDelivery')!=ids[pi:]:
        raise ValueError('Route legs disagree with frozen mission')
    dirs = []
    line_edges = line_checkpoint_edges(m) if is_line_demo(p) else None
    for i, w in enumerate(waypoints):
        if is_line_demo(p) and not 0<=w['arucoId']<50:
            raise ValueError('Route includes unprinted transit markers; select a directly mapped line-demo route')
        marker = by_id.get(w['arucoId'], {})
        if any(w.get(k)!=marker.get(k) for k in ('row','col','isIntersection')):
            raise ValueError('Route waypoint differs from commissioned physical scene')
        if i:
            d = direction(waypoints[i-1], w)
            if w.get('travelDirection')!=d:
                raise ValueError('Route direction mismatch')
            graph=m if is_line_demo(p) else p
            edge = (ids[i-1], ids[i]) if line_edges is not None else ['QR'+str(ids[i-1]), 'QR'+str(ids[i])]
            edges = line_edges if line_edges is not None else graph['links']
            if edge not in edges and (graph.get('directedLinks') or edge[::-1] not in edges):
                raise ValueError('Uncommissioned physical corridor')
            dirs.append(d)
    if len(set(dirs))!=1:
        raise ValueError('This physical demo supports task-selected collinear routes; curved turns are not commissioned yet')
    if waypoints[0]['isIntersection'] or waypoints[pi]['isIntersection'] or waypoints[-1]['isIntersection']:
        raise ValueError('Pickup/delivery/start must be outside reserved intersection')
    if not is_line_demo(p) and abs(wrap(heading-HEADINGS[dirs[0]])) > 8:
        raise ValueError('Robot must face the route; Ackermann robot cannot rotate in place')
    return dirs[0]
