"""PostgreSQL commissioning tests in disposable clones on smartfleet-charging.
The running main database is read only (pg_dump). Never rewrites operational data.
"""
import json
import os
from pathlib import Path
import subprocess
import uuid
import argparse
from datetime import datetime

ROOT=Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--project',default='smartfleet-charging-final')
parser.add_argument('--source-dump',type=Path,help='Legacy demo v5 snapshot, required if the main database was already upgraded')
args=parser.parse_args()
TARGET=args.project
assert TARGET.startswith('smartfleet-charging'), 'Clones must run in an isolated charging test database server'
OUT=Path(os.environ['LOCALAPPDATA'])/'SmartFleet/charging-validation'
OUT.mkdir(parents=True,exist_ok=True)
dump=args.source_dump.read_bytes() if args.source_dump else subprocess.run(['docker','compose','-p','smartfleet','exec','-T','database','pg_dump','-Fc','-U','smartfleet','-d','smartfleet'],capture_output=True,cwd=ROOT,check=True).stdout
if not args.source_dump:(OUT/('main-before-lane-upgrade-'+datetime.now().strftime('%Y%m%d-%H%M%S')+'.dump')).write_bytes(dump)
old=json.loads((ROOT/'simulation/config/map.json').read_text())
lane=json.loads((ROOT/'simulation/config/dual-lane-map.json').read_text())
def var(name,data):return "\\set "+name+" '"+json.dumps(data,separators=(',',':')).replace("'","''")+"'\n"
migration=(ROOT/'deploy/commission-dual-lane.sql').read_text()
migration=migration.replace('\\ir commission-demo-stations.sql',(ROOT/'deploy/commission-demo-stations.sql').read_text())
migration='\n'.join(l for l in migration.splitlines() if not l.startswith('\\set lane_map ') and not l.startswith('\\set legacy_map '))
migration=var('demo_map_json',old)+var('lane_map',lane)+var('legacy_map',old)+migration
command=['docker','compose','-p',TARGET,'exec','-T','database','psql','-X','-qAt','-v','ON_ERROR_STOP=1','-U','smartfleet']


def sql(db,text,success=True):
    result=subprocess.run(command+['-d',db],input=text,capture_output=True,encoding='utf-8',cwd=ROOT)
    assert (result.returncode==0)==success,result.stderr[-1500:]
    return result.stdout.strip()


def snapshot(db):
    tables=['Robots','TransportTasks','TaskAssignments','Missions','MissionExecutions','VerificationAttempts','DeliveryVerifications','RobotQrScanEvents','Stations','StationCheckpoints','StationBerths','MapConfigurations','SystemAuditLogs','smartfleet_demo_bootstrap']
    return {t:sql(db,f'SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),\'[]\'::jsonb) FROM "{t}" t;') for t in tables}


for case in ['valid_idempotent','active','failed_hold','berth','custom_corridor']:
    db='sf_lanes_'+uuid.uuid4().hex[:12]
    assert db.startswith('sf_lanes_')
    sql('postgres',f'CREATE DATABASE "{db}";')
    try:
        subprocess.run(['docker','compose','-p',TARGET,'exec','-T','database','pg_restore','--no-owner','-U','smartfleet','-d',db],input=dump,capture_output=True,cwd=ROOT,check=True)
        sql(db,(ROOT/'Database/20261127_charging_jobs.sql').read_text())
        assert sql(db,'SELECT count(*) FROM smartfleet_demo_bootstrap WHERE version=6;')=='0','Provide --source-dump pointing to a legacy v5 demo backup'
        assert sql(db,'SELECT count(*) FROM "TaskAssignments" WHERE "AssignmentStatus"=\'ACTIVE\';')=='0','Source snapshot has active work; use a clean fixture instead'
        if case in ['active','failed_hold']:
            status='EXECUTING' if case=='active' else 'FAILED'
            sql(db,f'''INSERT INTO "TransportTasks"("TaskID","TaskTrackingCode","WarehouseID","PickupStationID","DeliveryStationID","TaskStatus","CreatedBy") VALUES(900001,'LANE-SAFETY',1,1,2,'{status}',2);
              INSERT INTO "TaskAssignments"("AssignmentID","TaskID","RobotID","AssignmentStatus") VALUES(900001,900001,1,'ACTIVE');''')
        if case=='berth':sql(db,'INSERT INTO "StationBerths" VALUES(1,1,now());')
        if case=='custom_corridor':sql(db,'''UPDATE "MapConfigurations" SET "MapMetadataJson"=jsonb_set("MapMetadataJson"::jsonb,'{navigationMap,links}',("MapMetadataJson"::jsonb->'navigationMap'->'links')-(-1))::text WHERE "IsActive";''')
        before=snapshot(db)
        sql(db,migration,success=case=='valid_idempotent')
        after=snapshot(db)
        if case=='valid_idempotent':
            for table in ['Robots','TransportTasks','TaskAssignments','Missions','MissionExecutions','VerificationAttempts','DeliveryVerifications','RobotQrScanEvents','StationBerths']:
                assert before[table]==after[table],table
            assert sql(db,'SELECT count(*) FROM "Stations" WHERE "StationType"=\'CHARGING\';')=='4'
            assert sql(db,'SELECT "MapMetadataJson"::jsonb->\'navigationMap\'->>\'directedLinks\' FROM "MapConfigurations" WHERE "IsActive";')=='true'
            sql(db,migration);assert snapshot(db)==after,'Repeat upgrade must be idempotent'
        else:assert before==after,'Rejected geometry upgrade must be atomic'
        print('PASS:',case,flush=True)
    finally:sql('postgres',f'DROP DATABASE "{db}" WITH (FORCE);')
print('PASS: geometry commissioning preserves history; active work, holds, reservations and customized corridors rejected')
