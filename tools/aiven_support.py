"""Private shared-DB configuration and short-lived PostgreSQL CLI sessions."""
import ast
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def read_env():
    values = {}
    for line in (ROOT / '.env').read_text(encoding='utf-8').splitlines():
        if not line.strip() or line.lstrip().startswith('#') or '=' not in line: continue
        k, v = line.split('=', 1)
        if len(v) > 1 and v[0] in "\"'" and v[-1] == v[0]:
            try: v = ast.literal_eval(v)
            except (ValueError, SyntaxError): raise ValueError('Invalid private .env quoting for ' + k) from None
        values[k] = v
    return values


def update_env(values):
    path = ROOT / '.env'; lines = path.read_text(encoding='utf-8').splitlines()
    lines = [line for line in lines if line.split('=', 1)[0] not in values]
    for key, value in values.items():
        value = str(value)
        if any(c in value for c in '\r\n'): raise ValueError('Multiline environment values are not supported')
        quoted = "'" + value.replace('\\', '\\\\').replace("'", "\\'") + "'"
        lines.append(key + '=' + quoted)
    path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


def private_dir():
    base = Path(os.environ.get('LOCALAPPDATA', Path.home() / '.local/share'))
    out = base / 'SmartFleet/aiven-migration'; out.mkdir(parents=True, exist_ok=True); return out


def pg_command(tool, args=(), input=None, output=None):
    config = read_env(); env = os.environ.copy()
    env.update(PGHOST=config['AIVEN_HOST'], PGPORT=config['AIVEN_PORT'], PGUSER=config['AIVEN_USER'],
        PGPASSWORD=config['AIVEN_PASSWORD'], PGDATABASE=config['AIVEN_DATABASE'], PGSSLMODE='verify-full',
        PGSSLROOTCERT='/run/secrets/aiven-ca.pem', PGOPTIONS='-c statement_timeout=30000 -c application_name=SmartFleet-map-management')
    ca = str((ROOT / config['AIVEN_CA_FILE']).resolve())
    command = ['docker', 'run', '--rm', '-i', '--mount', f'type=bind,source={ca},target=/run/secrets/aiven-ca.pem,readonly']
    for key in ['PGHOST', 'PGPORT', 'PGUSER', 'PGPASSWORD', 'PGDATABASE', 'PGSSLMODE', 'PGSSLROOTCERT', 'PGOPTIONS']:
        command.extend(['-e', key])
    command += ['postgres:18.6-alpine', tool, *args]
    result = subprocess.run(command, input=input, stdout=output or subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    if result.returncode:
        detail = result.stderr.decode(errors='replace').replace(config['AIVEN_PASSWORD'], '[hidden]')
        raise RuntimeError('PostgreSQL operation failed: ' + detail[:900])
    return result.stdout.decode().strip() if output is None else None


def sql(text):
    return pg_command('psql', ['-X', '-v', 'ON_ERROR_STOP=1', '-tA'], text.encode())


def backup(label):
    path = private_dir() / (label + '-' + time.strftime('%Y%m%d-%H%M%S') + '.dump')
    with path.open('wb') as output: pg_command('pg_dump', ['-Fc'], output=output)
    return path


def quoted(value):
    return "'" + str(value).replace("'", "''") + "'"


def map_matches_scene(document, manifest):
    """Same physical contract as SimulationScene.Matches; labels are not geometry."""
    try:
        cells = [(cell, row, col) for row, line in enumerate(document['cells']) for col, cell in enumerate(line) if cell['type'] == 'qr']
        ids = {cell['arucoId'] for cell, _, _ in cells}
        if len(cells) != len(ids) or len(cells) != len(manifest['anchors']): return False
        if document.get('directedLinks', False) != manifest.get('directedLinks', False): return False
        if document.get('markerDictionary', 'DICT_4X4_50') != manifest.get('markerDictionary', 'DICT_4X4_50'): return False
        for anchor in manifest['anchors']:
            cell, row, col = next(x for x in cells if x[0]['arucoId'] == anchor['arucoId'])
            if row != anchor['row'] or col != anchor['col']: return False
            if abs(cell['markerYawDeg'] - anchor['markerYawDeg']) >= .01 or abs(cell['markerSizeM'] - anchor['markerSizeM']) >= .00001: return False
            if anchor.get('operationalRole') is not None and cell.get('operationalRole') != anchor['operationalRole']: return False
        if {c['arucoId'] for c, _, _ in cells if c.get('isIntersection', False)} != set(manifest['intersections']): return False
        labels = {cell['label']: cell['arucoId'] for cell, _, _ in cells}
        ordered = tuple if document.get('directedLinks', False) else lambda edge: tuple(sorted(edge))
        actual = {ordered((labels[a], labels[b])) for a, b in document['links'] if a in labels and b in labels}
        return actual == {ordered(edge) for edge in manifest['corridors']}
    except (KeyError, TypeError, ValueError, StopIteration): return False


STATION_SNAPSHOT = '''SELECT coalesce(jsonb_agg(jsonb_build_object(
 'stationId',s."StationID",'stationName',s."StationName",'stationType',s."StationType",
 'isActive',s."IsActive",'coordX',s."CoordX",'coordY',s."CoordY",'qrCodePayload',s."QrCodePayload",
 'arucoId',p."ArucoId") ORDER BY s."StationID"),'[]'::jsonb)
 FROM "Stations" s JOIN "Zones" z ON z."ZoneID"=s."ZoneID"
 LEFT JOIN "StationCheckpoints" p ON p."StationId"=s."StationID" WHERE z."WarehouseID"=1'''
