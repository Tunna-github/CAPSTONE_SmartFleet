"""Build an explicitly commissioned alternative scene; never edits a running map.

Each of the original nine QR points has a circulation ring. Nine transfer bays
sit inside rings, outside through lanes. Charging bays are
pull-through between separate entry/exit buses. All dimensions are metres.
"""
import hashlib
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]


def build():
    import cv2
    import numpy as np
    root = ROOT / 'simulation'
    old = json.loads((root / 'config/scene-manifest.json').read_text())
    area_names = json.loads((root / 'config/transfer-area-names.json').read_text(encoding='utf-8'))
    original = (root / 'worlds/smartfleet.wbt').read_text()
    anchors, by_pos, edges = {}, {}, []
    next_id = 9

    def point(r, c, marker=None, role='NAVIGATION', intersection=False):
        nonlocal next_id
        if (r, c) in by_pos:
            return by_pos[r, c]
        if marker is None:
            marker, next_id = next_id, next_id + 1
        anchors[marker] = dict(arucoId=marker, row=r, col=c,
            worldX=(r - 7) * .5, worldY=(c - 10) * .5,
            markerYawDeg=-90., markerSizeM=.04,
            operationalRole=role, isIntersection=intersection)
        by_pos[r, c] = marker
        return marker

    def link(a, b):
        assert a != b
        assert anchors[a]['row'] == anchors[b]['row'] or anchors[a]['col'] == anchors[b]['col']
        if [a, b] not in edges:
            edges.append([a, b])

    order = [[3, 2, 8], [4, 1, 7], [5, 0, 6]]
    rings = {}
    for ri, row in enumerate(order):
        for ci, marker in enumerate(row):
            r, c = 3 + ri * 4, 4 + ci * 6
            center = point(r, c, marker, 'TRANSFER')
            nw, north, ne, se, sw = [point(y, x) for y, x in
                [(r-1,c-1),(r-1,c),(r-1,c+1),(r+1,c+1),(r+1,c-1)]]
            rings[ri, ci] = (nw, north, ne, se, sw)
            for a, b in zip([nw,north,ne,se,sw], [north,ne,se,sw,nw]):
                link(a,b)
            # Shared bay spur is protected by current+next reservations. Never
            # stop a transport robot on either through lane for handover.
            link(north, center)
            link(center, north)
    for (ri, ci), (nw, n, ne, se, sw) in rings.items():
        if ci < 2:
            other = rings[ri, ci+1]
            link(ne, other[0])  # east lane
            link(other[4], se)  # west lane
        if ri < 2:
            other = rings[ri+1, ci]
            link(se, other[2])  # south lane
            link(other[0], sw)  # north lane

    # Charging zone: four off-road, physically independent pull-through bays.
    incoming = [point(15, c) for c in [3,5,8,11,14,17]]
    outgoing = [point(17, c) for c in [17,14,11,8,5,1]]
    for bus in [incoming, outgoing]:
        for a,b in zip(bus,bus[1:]): link(a,b)
    link(rings[2,0][4],incoming[0])
    link(incoming[-1],outgoing[0])
    exit_node = point(12,1)
    link(outgoing[-1],exit_node)
    link(exit_node,rings[2,0][4])
    bays = []
    for i,c in enumerate([5,8,11,14],1):
        berth = point(16,c,role='CHARGING')
        link(by_pos[15,c],berth);link(berth,by_pos[17,c])
        bays.append(dict(code=f'CHG{i}',arucoId=berth,capacity=1))
    assert max(anchors) < 250
    cells = [[dict(type='floor',label=None,arucoId=None,isIntersection=False) for _ in range(28)] for _ in range(18)]
    for a in anchors.values():
        cells[a['row']][a['col']] = {k:a[k] for k in ['arucoId','markerYawDeg','markerSizeM','operationalRole','isIntersection']} | dict(type='qr',label=f"QR{a['arucoId']}")
        if str(a['arucoId']) in area_names:
            cells[a['row']][a['col']]['displayName'] = area_names[str(a['arucoId'])]
    doc = dict(rows=18,columns=28,revision=1,cells=cells,
        directedLinks=True,markerDictionary='DICT_4X4_250',links=[[f'QR{a}',f'QR{b}'] for a,b in edges])
    (root / 'config/dual-lane-map.json').write_text(json.dumps(doc,indent=2)+'\n')

    textures = root / 'worlds/textures/dual-lane'
    textures.mkdir(exist_ok=True)
    dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_250)
    # Existing IDs use the same bit patterns across OpenCV 4x4 dictionaries.
    assert np.array_equal(dictionary.bytesList[:50],cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50).bytesList)
    for marker in anchors:
        cv2.imwrite(str(textures / f'QR{marker}.png'),cv2.aruco.generateImageMarker(dictionary,marker,512))
    header = original[:original.index('WorldInfo')]
    world = header + '''WorldInfo { basicTimeStep 64 }
Viewpoint { orientation 0.50 0 -0.866 3.14159 position 10 0 14 }
TexturedBackground { }
TexturedBackgroundLight { castShadows FALSE }
Solid { translation 0 0 -0.05 children [ Shape { appearance PBRAppearance { baseColor 0.89 0.91 0.93 roughness 1 metalness 0 } geometry Box { size 14 14 0.1 } } ] boundingObject Box { size 14 14 0.1 } name "warehouse-floor" }
'''
    for index,(x,y,sx,sy) in enumerate([(7,0,.1,14),(-7,0,.1,14),(0,7,14,.1),(0,-7,14,.1)]):
        world += f'Solid {{ translation {x} {y} .2 children [ Shape {{ appearance PBRAppearance {{ baseColor .6 .65 .7 }} geometry Box {{ size {sx} {sy} .4 }} }} ] boundingObject Box {{ size {sx} {sy} .4 }} name "warehouse-wall-{index}" }}\n'
    # Painted road ribbons, not walls: geometry matches the commissioned graph.
    for a,b in edges:
        if a > b and [b,a] in edges: continue
        p,q = anchors[a],anchors[b]
        x,y = (p['worldX']+q['worldX'])/2,(p['worldY']+q['worldY'])/2
        sx,sy = max(.25,abs(p['worldX']-q['worldX'])),max(.25,abs(p['worldY']-q['worldY']))
        world += f'Solid {{ translation {x} {y} 0.001 children [ Shape {{ appearance PBRAppearance {{ baseColor 0.70 0.78 0.81 roughness 1 metalness 0 }} geometry Plane {{ size {sx} {sy} }} }} ] name "lane-{a}-{b}" }}\n'
    for a in anchors.values():
        m,x,y = a['arucoId'],a['worldX'],a['worldY']
        if a['operationalRole'] in ['TRANSFER','CHARGING']:
            color = '0.55 0.86 0.63' if a['operationalRole']=='CHARGING' else '0.61 0.79 0.94'
            world += f'Solid {{ translation {x} {y} 0.002 children [ Shape {{ appearance PBRAppearance {{ baseColor {color} roughness 1 metalness 0 }} geometry Plane {{ size 0.40 0.40 }} }} ] name "bay-{m}" }}\n'
        world += f'Solid {{ translation {x} {y} 0.005 rotation 0 0 1 1.5707963267948966 children [ Shape {{ appearance PBRAppearance {{ baseColorMap ImageTexture {{ url [ "textures/dual-lane/QR{m}.png" ] }} metalness 0 roughness 1 }} geometry Plane {{ size 0.04 0.04 }} }} ] name "QR{m}" }}\n'
    for code,marker in [('R01',0),('R02',67)]:
        # Existing validated robot and downward camera assembly. This is only
        # initial placement; heading is determined from camera + IMU.
        block = re.search(r'DEF SF_'+code+r' TurtleBot3Burger \{.*?\n\}',original,re.S).group()
        a = anchors[marker]
        block = re.sub(r'translation [^\n]+',f"translation {a['worldX']+.075} {a['worldY']} -0.0004935372",block,count=1)
        block = re.sub(r'rotation [^\n]+','rotation 0 0 1 3.141592653589793',block,count=1)
        block = block.replace('--traffic-test-hold=8','--traffic-test-hold=0')
        world += block+'\n'
    world += 'Robot { name "SmartFleet simulation observer" supervisor TRUE controller "smartfleet_observer" }\n'
    path = root/'worlds/smartfleet-dual-lane.wbt'
    path.write_text(world,encoding='utf-8',newline='\n')
    assets = {f'textures/dual-lane/QR{i}.png':hashlib.sha256((textures/f'QR{i}.png').read_bytes()).hexdigest() for i in anchors}
    manifest = dict(sceneId='smartfleet-dual-lane-r2025a-v2',warehouseId=1,
        world='worlds/smartfleet-dual-lane.wbt',worldSha256=hashlib.sha256(path.read_bytes()).hexdigest(),
        mapFile='config/dual-lane-map.json',mapRevisionAtExport=1,
        directedLinks=True,markerDictionary='DICT_4X4_250',metersPerGridCell=.5,
        worldToGrid=[[0,2,10],[2,0,7]],worldToMapYawDeg=180.,
        anchors=[{k:v for k,v in a.items() if k != 'isIntersection'} for a in sorted(anchors.values(),key=lambda a:a['arucoId'])],
        corridors=edges,intersections=[],assets=assets,robots=old['robots'],chargingBays=bays)
    (root/'config/dual-lane-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print(f'Built {len(anchors)} markers, {len(edges)} directed links, four charging bays. Existing nine station IDs retained.')


if __name__ == '__main__': build()
