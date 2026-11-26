"""Durable command ordering for the simulator's empty-robot charging workflow."""
import json
import os


class ChargingJournal:
    def __init__(self, path):
        self.path = path
        self.saved = {}
        if os.path.exists(path):
            try:
                with open(path, encoding='utf-8') as f: self.saved = json.load(f)
                if not isinstance(self.saved.get('generation'), int): raise ValueError('generation')
            except (ValueError, OSError, TypeError):
                # Never interpret a corrupt persisted motion journal as idle.
                self.saved = {'generation': 2147483647, 'state': 'FAILED'}

    def save(self, data, state):
        self.saved = {'chargingJobId': data['chargingJobId'], 'generation': data['generation'],
                      'state': state}
        temp = self.path + '.tmp'
        with open(temp, 'w', encoding='utf-8') as f: json.dump(self.saved, f)
        os.replace(temp, self.path)

    def accepts_new(self, data):
        gen = data.get('generation')
        return (isinstance(gen, int) and not isinstance(gen, bool) and gen > 0 and
                gen > self.saved.get('generation', 0))


def validate_route(data, marker, map_revision):
    import uuid
    uuid.UUID(data['chargingJobId'])
    route = data.get('route', {})
    points = route.get('waypoints')
    if route.get('mapRevision') != map_revision or route.get('startArUcoId') != marker:
        raise ValueError('Charging route requires fresh localization on its map revision')
    if not isinstance(points, list) or not 1 <= len(points) <= 100:
        raise ValueError('Invalid charging waypoint count')
    if points[0].get('arucoId') != marker or points[-1].get('arucoId') != route.get('homeArUcoId'):
        raise ValueError('Charging route endpoints mismatch')
    for p in points:
        if (not isinstance(p.get('arucoId'), int) or not 0 <= p['arucoId'] < 250 or
                not isinstance(p.get('row'), int) or not 0 <= p['row'] < 18 or
                not isinstance(p.get('col'), int) or not 0 <= p['col'] < 28 or
                not isinstance(p.get('isIntersection'), bool)):
            raise ValueError('Charging waypoint is uncommissioned')
    for a,b in zip(points,points[1:]):
        direction = ('E' if b['col']>a['col'] else 'W') if a['row']==b['row'] and a['col']!=b['col'] else (
            ('S' if b['row']>a['row'] else 'N') if a['col']==b['col'] and a['row']!=b['row'] else None)
        if direction is None or b.get('travelDirection') != direction:
            raise ValueError('Charging route must follow a calibrated straight lane')
    return points
