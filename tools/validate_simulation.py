"""Validate the portable scene without importing Webots or requiring a display."""
import hashlib
import json
from pathlib import Path
import re


def validate(root, manifest_name="config/scene-manifest.json"):
    root = Path(root)
    manifest = json.loads((root / manifest_name).read_text(encoding="utf-8"))
    assert hashlib.sha256((root / manifest["world"]).read_bytes()).hexdigest() == manifest["worldSha256"], "World hash mismatch"
    for name, expected in manifest["assets"].items():
        assert hashlib.sha256((root / "worlds" / name).read_bytes()).hexdigest() == expected, "Asset mismatch: " + name
    world = (root / manifest["world"]).read_text(encoding="utf-8")
    assert not re.search(r'"[A-Za-z]:[/\\]', world), "Absolute Windows path in world"
    assert "--start-heading" not in world and "--start-qr" not in world
    for url in re.findall(r'"(https?://[^" ]+)"', world):
        assert "/R2025a/" in url, "Unpinned Webots dependency"
    saved_map = json.loads((root / manifest.get("mapFile", "config/map.json")).read_text(encoding="utf-8"))
    qr_cells = {cell["label"]: cell for row in saved_map["cells"] for cell in row if cell["type"] == "qr"}
    ordered = tuple if manifest.get('directedLinks', False) else lambda x: tuple(sorted(x))
    assert saved_map.get('directedLinks',False) == manifest.get('directedLinks',False)
    assert saved_map.get('markerDictionary','DICT_4X4_50') == manifest.get('markerDictionary','DICT_4X4_50')
    corridors = {ordered((qr_cells[a]["arucoId"], qr_cells[b]["arucoId"]))
                 for a, b in saved_map["links"] if a in qr_cells and b in qr_cells}
    assert corridors and corridors == {ordered(edge) for edge in manifest["corridors"]}, "Commissioned corridor mismatch"
    assert {cell["arucoId"] for cell in qr_cells.values() if cell["isIntersection"]} == set(manifest["intersections"]), "Commissioned intersection mismatch"
    for anchor in manifest["anchors"]:
        cell = saved_map["cells"][anchor["row"]][anchor["col"]]
        assert cell["arucoId"] == anchor["arucoId"]
        for axis, target in zip(manifest["worldToGrid"], [anchor["col"], anchor["row"]]):
            projected = axis[0]*anchor["worldX"] + axis[1]*anchor["worldY"] + axis[2]
            assert abs(projected-target) < .04, "Map/world transform mismatch"
    return manifest


if __name__ == "__main__":
    import sys
    scene = validate(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / "simulation",sys.argv[2] if len(sys.argv)>2 else "config/scene-manifest.json")
    print("Validated", scene["sceneId"], "with", len(scene["anchors"]), "anchors")
