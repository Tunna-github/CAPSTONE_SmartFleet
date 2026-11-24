"""Export installation calibration from a Webots world and an existing saved map.

Does not change PostgreSQL, the world, or robot poses. Review output before import.
Marker geometry and texture IDs must agree with the map's physical grid.
"""
import argparse
import json
import math
from pathlib import Path
import re
import cv2
import numpy as np


def commission(world, document):
    detector = cv2.aruco.ArucoDetector(cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50))
    physical = {}
    for block in re.split(r"(?m)^Solid \{", world.read_text(encoding="utf-8"))[1:]:
        texture = re.search(r'"(textures/[^"\n]+\.png)"', block)
        if not texture: continue
        image = cv2.imread(str(world.parent / texture[1]), cv2.IMREAD_GRAYSCALE)
        if image is None: raise ValueError("Missing marker texture " + texture[1])
        _, ids, _ = detector.detectMarkers(cv2.copyMakeBorder(image, 50, 50, 50, 50, cv2.BORDER_CONSTANT, value=255))
        if ids is None or len(ids) != 1: raise ValueError("Texture must contain one known ArUco marker")
        marker_id = int(ids.reshape(-1)[0])
        xyz = [float(v) for v in re.search(r"translation ([^\n]+)", block)[1].split()]
        axis_angle = [float(v) for v in re.search(r"rotation ([^\n]+)", block)[1].split()]
        axis = np.array(axis_angle[:3]); axis /= np.linalg.norm(axis)
        rotation = cv2.Rodrigues(axis * axis_angle[3])[0]
        if rotation[2, 2] < 0.999: raise ValueError("Only flat floor markers are supported by this importer")
        size = [float(v) for v in re.search(r"size ([^\n]+)", block)[1].split()]
        if len(size) != 2 or abs(size[0] - size[1]) > 1e-6: raise ValueError("Marker plane must be square")
        if marker_id in physical: raise ValueError("Repeated physical marker ID")
        physical[marker_id] = {"xy": xyz[:2], "yaw": math.atan2(rotation[1, 0], rotation[0, 0]), "size": size[0]}
    points = [(cell, r, c) for r, row in enumerate(document["cells"]) for c, cell in enumerate(row) if cell["type"] == "qr"]
    if len(points) < 3 or any(cell["arucoId"] not in physical for cell, _, _ in points):
        raise ValueError("Need at least three mapped markers present in this world")
    world_xy = np.array([physical[cell["arucoId"]]["xy"] for cell, _, _ in points])
    map_xy = np.array([[-r, -c] for _, r, c in points], dtype=float)  # N, W
    a = world_xy - world_xy.mean(axis=0)
    b = map_xy - map_xy.mean(axis=0)
    if np.linalg.matrix_rank(a) != 2: raise ValueError("Markers must span two map axes")
    u, singular, vt = np.linalg.svd(a.T @ b)
    rotation = vt.T @ u.T
    if np.linalg.det(rotation) < 0.99: raise ValueError("Map grid is mirrored relative to the physical marker layout")
    scale = float(singular.sum() / np.square(a).sum())
    residual = float(np.sqrt(np.square((a @ rotation.T) * scale - b).sum(axis=1).mean()))
    if residual > 0.08: raise ValueError("Map and world marker geometry disagree: {:.3f} grid cells".format(residual))
    offset = math.atan2(rotation[1, 0], rotation[0, 0])
    for cell, _, _ in points:
        item = physical[cell["arucoId"]]
        cell["markerYawDeg"] = (math.degrees(item["yaw"] + offset) + 180) % 360 - 180
        cell["markerSizeM"] = item["size"]
    return {"markerCount": len(points), "worldToMapYawDeg": math.degrees(offset), "gridResidual": residual}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--world", type=Path, required=True)
    parser.add_argument("--map", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    document = json.loads(args.map.read_text(encoding="utf-8-sig"))
    report = commission(args.world, document)
    args.output.write_text(json.dumps(document, indent=2), encoding="utf-8")
    print(json.dumps(report))
