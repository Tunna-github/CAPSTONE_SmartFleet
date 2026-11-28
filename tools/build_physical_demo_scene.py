"""Export an explicit Webots counterpart of a measured physical 3x3 installation.

Writes only a NEW output bundle. Does not touch Aiven, the active map, a running
world, or the real robot. R01 remains physical; only R02 is spawned in Webots.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import sys

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'Robot code'/'physical'))
from protocol import validate_profile
from line_vision import is_line_demo


def build(profile,output):
    if is_line_demo(profile):raise ValueError('Marker/line events have no metric scene geometry to export; keep existing Webots world/manifest validation')
    validate_profile(profile)
    output=Path(output).resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError('Output must be a new/empty directory; existing scenes are never overwritten')
    import cv2
    base=(ROOT/'simulation'/'worlds'/'smartfleet.wbt').read_text()
    output.mkdir(parents=True,exist_ok=True)
    for directory in ('config','worlds/textures/physical-demo'):(output/directory).mkdir(parents=True)
    scale=profile['metersPerGridCell']
    markers=profile['markers']
    center_r=(min(m['row'] for m in markers)+max(m['row'] for m in markers))/2
    center_c=(min(m['col'] for m in markers)+max(m['col'] for m in markers))/2
    by_id={m['arucoId']:m for m in markers}
    grid=[[dict(type='floor') for _ in range(28)] for _ in range(18)]
    anchors=[]
    assets={}
    header=base[:base.index('WorldInfo')]
    side=max((max(m['row'] for m in markers)-min(m['row'] for m in markers))*scale,
             (max(m['col'] for m in markers)-min(m['col'] for m in markers))*scale)+2
    world=header+'''WorldInfo { basicTimeStep 64 }
Viewpoint { orientation 0.5 0 -0.866 3.14159 position 4 0 6 }
TexturedBackground { }
TexturedBackgroundLight { castShadows FALSE }
'''
    world+='Solid { translation 0 0 -.05 children [ Shape { appearance PBRAppearance { baseColor .95 .95 .95 roughness 1 metalness 0 } geometry Box { size '+f'{side} {side} .1'+' } } ] boundingObject Box { size '+f'{side} {side} .1'+' } name "physical-demo-floor" }\n'
    dictionary=cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    for m in markers:
        ident=m['arucoId']
        x,y=(m['row']-center_r)*scale,(m['col']-center_c)*scale
        cell=dict(m,type='qr',label='QR'+str(ident),operationalRole='NAVIGATION' if m['isIntersection'] else 'TRANSFER')
        grid[m['row']][m['col']]=cell
        anchors.append(dict(m,worldX=x,worldY=y))
        image=cv2.aruco.generateImageMarker(dictionary,ident,512) if hasattr(cv2.aruco,'generateImageMarker') else cv2.aruco.drawMarker(dictionary,ident,512)
        asset='textures/physical-demo/QR{}.png'.format(ident)
        cv2.imwrite(str(output/'worlds'/asset),image)
        assets[asset]=hashlib.sha256((output/'worlds'/asset).read_bytes()).hexdigest()
        angle=math.radians(m['markerYawDeg']+180)
        world+=f'Solid {{ translation {x} {y} .005 rotation 0 0 1 {angle} children [ Shape {{ appearance PBRAppearance {{ baseColorMap ImageTexture {{ url [ "{asset}" ] }} roughness 1 metalness 0 }} geometry Plane {{ size {m["markerSizeM"]} {m["markerSizeM"]} }} }} ] name "QR{ident}" }}\n'
    # Simulation camera is raised to see full-size physical markers. Its optical
    # rotation/FOV/resolution are unchanged, so heading installation stays valid.
    block=re.search(r'DEF SF_R02 TurtleBot3Burger \{.*?\n\}',base,re.S)
    if block is None or 'translation 0.080866 0.0077265 0.0906544' not in block.group():
        raise ValueError('Simulation template camera changed; review exporter')
    start=by_id.get(1)
    if start is None or start['isIntersection']:
        raise ValueError('Mixed demo expects a non-intersection QR1 start for simulation R02')
    x,y=(start['row']-center_r)*scale,(start['col']-center_c)*scale
    robot=re.sub(r'translation [^\n]+',f'translation {x-.080866} {y-.0077265} -0.0004935372',block.group(),count=1)
    robot=re.sub(r'rotation [^\n]+','rotation 0 0 1 0',robot,count=1)
    robot=robot.replace('translation 0.080866 0.0077265 0.0906544',f'translation 0.080866 0.0077265 {max(.6,max(m["markerSizeM"] for m in markers)*3.5)}')
    world+=robot+'\nRobot { name "SmartFleet simulation observer" supervisor TRUE controller "smartfleet_observer" }\n'
    world_path=output/'worlds'/'smartfleet-physical-demo.wbt'
    world_path.write_text(world,encoding='utf-8',newline='\n')
    doc=dict(revision=1,rows=18,columns=28,cells=grid,links=profile['links'],directedLinks=profile.get('directedLinks',False),markerDictionary='DICT_4X4_50')
    (output/'config'/'physical-demo-map.json').write_text(json.dumps(doc,indent=2))
    manifest=dict(sceneId='smartfleet-physical-demo',warehouseId=profile.get('warehouseId',1),
        world='worlds/smartfleet-physical-demo.wbt',worldSha256=hashlib.sha256(world_path.read_bytes()).hexdigest(),
        mapFile='config/physical-demo-map.json',mapRevisionAtExport=1,markerDictionary='DICT_4X4_50',
        directedLinks=profile.get('directedLinks',False),metersPerGridCell=scale,
        worldToGrid=[[0,1/scale,center_c],[1/scale,0,center_r]],worldToMapYawDeg=180,
        anchors=anchors,corridors=[[int(a[2:]),int(b[2:])] for a,b in profile['links']],
        intersections=[m['arucoId'] for m in markers if m['isIntersection']],assets=assets,
        robots=[dict(code='R02',defName='SF_R02')])
    (output/'config'/'physical-demo-manifest.json').write_text(json.dumps(manifest,indent=2))
    return manifest


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--profile',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=build(json.loads(args.profile.read_text()),args.output)
    print('Exported',result['sceneId'],'to',args.output,'; review/commission explicitly before activation.')
