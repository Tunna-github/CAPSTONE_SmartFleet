"""Validate the portable scene without importing Webots or requiring a display."""
import hashlib
import json
from pathlib import Path
import re


def validate(root):
    root = Path(root)
    manifest = json.loads((root / "config/scene-manifest.json").read_text(encoding="utf-8"))
    assert hashlib.sha256((root / manifest["world"]).read_bytes()).hexdigest() == manifest["worldSha256"], "World hash mismatch"
    for name, expected in manifest["assets"].items():
        assert hashlib.sha256((root / "worlds" / name).read_bytes()).hexdigest() == expected, "Asset mismatch: " + name
    world = (root / manifest["world"]).read_text(encoding="utf-8")
    assert not re.search(r'"[A-Za-z]:[/\\]', world), "Absolute Windows path in world"
    assert "--start-heading" not in world and "--start-qr" not in world
    for url in re.findall(r'"(https?://[^" ]+)"', world):
        assert "/R2025a/" in url, "Unpinned Webots dependency"
    saved_map = json.loads((root / "config/map.json").read_text(encoding="utf-8"))
    for anchor in manifest["anchors"]:
        cell = saved_map["cells"][anchor["row"]][anchor["col"]]
        assert cell["arucoId"] == anchor["arucoId"]
        for axis, target in zip(manifest["worldToGrid"], [anchor["col"], anchor["row"]]):
            projected = axis[0]*anchor["worldX"] + axis[1]*anchor["worldY"] + axis[2]
            assert abs(projected-target) < .04, "Map/world transform mismatch"
    return manifest


if __name__ == "__main__":
    import sys
    scene = validate(sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / "simulation")
    print("Validated", scene["sceneId"], "with", len(scene["anchors"]), "anchors")
