"""Configure an already restored Docker database on Aiven without merging old tasks.

Connection information and CA are read from an ignored native appsettings file.
The original Aiven map JSON is read from an explicit private export, never guessed.
"""
import argparse
import base64
import json
import os
from pathlib import Path
import shutil
import uuid
from aiven_support import ROOT, private_dir, quoted, sql, STATION_SNAPSHOT, update_env, map_matches_scene, read_env, backup
from mqtt_hotspot import keep_hotspot_overlay


def parse_connection_string(text):
    pairs = {}; key = ''; value = ''; in_value = False; quote = None; i = 0
    while i < len(text):
        c = text[i]
        if quote:
            if c == quote:
                if i + 1 < len(text) and text[i + 1] == quote: value += c; i += 1
                else: quote = None
            else: value += c
        elif not in_value and c == '=': in_value = True
        elif in_value and not value and c in "\"'": quote = c
        elif c == ';':
            if key.strip(): pairs[key.strip().lower()] = value.strip()
            key = ''; value = ''; in_value = False
        elif in_value: value += c
        else: key += c
        i += 1
    if quote: raise ValueError('Unterminated connection string value')
    if key.strip(): pairs[key.strip().lower()] = value.strip()
    return pairs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--settings', type=Path, default=ROOT / 'SmartFleetBE/appsettings.json')
    parser.add_argument('--database', default='smartfleet_shared')
    parser.add_argument('--map1-json', type=Path)
    parser.add_argument('--join-existing', action='store_true', help='Configure a second installation without importing or changing shared data')
    args = parser.parse_args()
    config = json.loads(args.settings.read_text(encoding='utf-8-sig'))
    connection = parse_connection_string(config['ConnectionStrings']['DefaultConnection'])
    certificate = Path(connection['root certificate'])
    if not certificate.is_file(): raise SystemExit('The configured Aiven CA certificate is missing')
    if not args.join_existing and args.map1_json is None: parser.error('--map1-json is required for initial migration')
    map1 = None
    if args.map1_json is not None:
        map1 = json.loads(args.map1_json.read_text(encoding='utf-8'))
        manifest = json.loads((ROOT / 'simulation/config/scene-manifest.json').read_text())
        if not map_matches_scene(map1, manifest): raise SystemExit('Map 1 requires a separately commissioned world; original geometry was not changed')
    original_env = private_dir() / 'env-before-aiven'
    if not original_env.exists(): original_env.write_bytes((ROOT / '.env').read_bytes())
    destination = ROOT / '.aiven'; destination.mkdir(exist_ok=True); shutil.copyfile(certificate, destination / 'ca.pem')
    def part(value): return '"' + str(value).replace('"', '""') + '"'
    values = dict(AIVEN_HOST=connection['host'], AIVEN_PORT=connection.get('port', '5432'), AIVEN_USER=connection['username'],
        AIVEN_PASSWORD=connection['password'], AIVEN_DATABASE=args.database, AIVEN_CA_FILE='./.aiven/ca.pem')
    values['AIVEN_CONNECTION_STRING'] = ';'.join(k + '=' + part(v) for k, v in {
        'Host': values['AIVEN_HOST'], 'Port': values['AIVEN_PORT'], 'Username': values['AIVEN_USER'],
        'Password': values['AIVEN_PASSWORD'], 'Database': args.database, 'SSL Mode': 'VerifyFull',
        'Root Certificate': '/run/secrets/aiven-ca.pem', 'Maximum Pool Size': 8, 'Minimum Pool Size': 0,
        'Keepalive': 10, 'TCP Keepalive': True, 'GSS Encryption Mode': 'Disable',
        'Options': '-c idle_session_timeout=60000',
        'Connection Idle Lifetime': 30, 'Timeout': 15, 'Command Timeout': 30, 'Application Name': 'SmartFleet-shared'
    }.items())
    update_env(values)
    if sql('''SELECT count(*) FROM information_schema.columns WHERE table_name='TaskInterventions' AND column_name='ResumedAt';''')=='0':
        backup('before-task-resume-schema')
        sql((ROOT/'Database/20261127_task_resume.sql').read_text())
    if not sql('SELECT to_regclass(\'public."MapSwitchControl"\');'):
        backup('before-web-map-manager-schema')
        sql((ROOT/'Database/20261127_web_map_switch.sql').read_text())
    update_env({'MAP_SWITCH_HOST_ID': read_env().get('MAP_SWITCH_HOST_ID') or str(uuid.uuid4())})
    if args.join_existing:
        key = sql('''SELECT p."Key" FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."IsSelected" AND m."IsActive";''')
        if key not in ('map1', 'map2'): raise SystemExit('The shared deployment has no single valid map profile')
        warehouse = sql('''SELECT m."WarehouseID" FROM "MapDeploymentProfiles" p JOIN "MapConfigurations" m ON m."MapID"=p."MapID" WHERE p."Key"=''' + quoted(key) + ';')
        update_env(dict(COMPOSE_FILE=os.pathsep.join(keep_hotspot_overlay(['compose.yaml', 'compose.charging.yaml', 'compose.aiven.yaml'],read_env())), AIVEN_WAREHOUSE_ID=warehouse,
            AIVEN_MAP_KEY=key, AIVEN_CHARGING_ENABLED='true' if key == 'map2' else 'false',
            AIVEN_MANIFEST='config/dual-lane-manifest.json' if key == 'map2' else 'config/scene-manifest.json',
            AIVEN_WORLD='worlds/smartfleet-dual-lane.wbt' if key == 'map2' else 'worlds/smartfleet.wbt',
            AIVEN_BACKEND_VOLUME='smartfleet-aiven-' + key + '-backend', AIVEN_SIMULATOR_VOLUME='smartfleet-aiven-' + key + '-simulator'))
        print('Joined shared Aiven configuration without modifying database data. Only one simulation host may run; other users should open its web address.')
        return
    assert sql("SELECT count(*) FROM smartfleet_demo_bootstrap WHERE version=7;") == '1', 'Requires the restored current Docker database'
    sql((ROOT / 'Database/20261127_shared_map_profiles.sql').read_text())
    if sql('SELECT count(*) FROM "MapDeploymentProfiles";') != '0': raise SystemExit('Profiles already exist; use select_aiven_map.py instead. No profile data overwritten.')
    encoded = base64.b64encode(json.dumps(map1).encode()).decode()
    sql('''BEGIN;
SELECT pg_advisory_xact_lock(490,20261128);
DO $$ BEGIN
 IF EXISTS(SELECT 1 FROM "TaskAssignments" WHERE "AssignmentStatus"='ACTIVE') THEN RAISE EXCEPTION 'Active mission; stop migration'; END IF;
END $$;
INSERT INTO "MapDeploymentProfiles" ("Key","MapID","DisplayName","SceneId","ChargingEnabled","StationSnapshotJson")
SELECT 'map2',"MapID",'Map 2 - Separated lanes and charging','smartfleet-dual-lane-r2025a-v2',true,(''' + STATION_SNAPSHOT + ''')::text
FROM "MapConfigurations" WHERE "WarehouseID"=1 AND "IsActive";
INSERT INTO "MapConfigurations" ("WarehouseID","MapVersion","MapName","MapSource","MapMetadataJson","IsActive")
VALUES (1,'aiven-original-map1','Map 1 - Original Aiven map','MANUAL',jsonb_build_object('navigationMap',convert_from(decode(''' + quoted(encoded) + ''','base64'),'UTF8')::jsonb)::text,false);
INSERT INTO "MapDeploymentProfiles" ("Key","MapID","DisplayName","SceneId","ChargingEnabled","StationSnapshotJson")
SELECT 'map1',m."MapID",'Map 1 - Original Aiven map','smartfleet-demo-r2025a-v1',false,
 (SELECT jsonb_agg(s.value || jsonb_build_object('isActive',(s.value->>'isActive')::boolean AND c.cell IS NOT NULL AND NOT coalesce((c.cell->>'isIntersection')::boolean,false),
 'arucoId',CASE WHEN c.cell IS NOT NULL AND NOT coalesce((c.cell->>'isIntersection')::boolean,false) THEN s.value->'arucoId' ELSE 'null'::jsonb END,
 'coordX',coalesce(c.col*.5,(s.value->>'coordX')::numeric),'coordY',coalesce(c.row*.5,(s.value->>'coordY')::numeric)))
 FROM "MapDeploymentProfiles" p CROSS JOIN LATERAL jsonb_array_elements(p."StationSnapshotJson"::jsonb) s
 LEFT JOIN LATERAL (SELECT cell.value AS cell,r.ordinality-1 AS row,cell.ordinality-1 AS col
 FROM jsonb_array_elements(m."MapMetadataJson"::jsonb->'navigationMap'->'cells') WITH ORDINALITY r
 CROSS JOIN LATERAL jsonb_array_elements(r.value) WITH ORDINALITY cell
 WHERE cell.value->>'type'='qr' AND cell.value->>'arucoId'=s.value->>'arucoId') c ON true WHERE p."Key"='map2')::text
FROM "MapConfigurations" m WHERE m."MapVersion"='aiven-original-map1';
COMMIT;''')
    backup('before-independent-map-warehouses')
    sql((ROOT / 'Database/20261127_independent_map_warehouses.sql').read_text())
    update_env(dict(COMPOSE_FILE=os.pathsep.join(keep_hotspot_overlay(['compose.yaml', 'compose.charging.yaml', 'compose.aiven.yaml'],read_env())),
        AIVEN_MAP_KEY='map2', AIVEN_WAREHOUSE_ID='1', AIVEN_CHARGING_ENABLED='true', AIVEN_MANIFEST='config/dual-lane-manifest.json',
        AIVEN_WORLD='worlds/smartfleet-dual-lane.wbt', AIVEN_BACKEND_VOLUME='smartfleet_backend-dual-lane-state',
        AIVEN_SIMULATOR_VOLUME='smartfleet_simulator-dual-lane-state'))
    print('Configured Map 2 on Aiven. Original Map 1 is stored separately; no old Aiven tasks were merged. Credentials were not printed.')


if __name__ == '__main__': main()
