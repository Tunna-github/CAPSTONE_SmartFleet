"""Populate the version-pinned Webots cache during image build, using HTTP/1.1.

Avoids R2025a Qt HTTP/2 download failures at runtime. Hashes are commissioned
in config/webots-assets.json; source/license URLs stay with the dependency list.
"""
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
from pathlib import Path
import sys
import time
import urllib.request

index = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
destination = Path(sys.argv[2])
destination.mkdir(parents=True, exist_ok=True)


def cache(item):
    url, expected = item["url"], item["sha256"]
    if not url.startswith("https://raw.githubusercontent.com/cyberbotics/webots/R2025a/"):
        raise ValueError("Unpinned simulation dependency")
    target = destination / hashlib.sha1(url.encode()).hexdigest()
    if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == expected:
        return
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=45) as response:
                data=response.read()
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError("Dependency checksum mismatch: " + url)
            target.write_bytes(data)
            return
        except Exception:
            if attempt == 2:
                raise
            time.sleep(1)


with ThreadPoolExecutor(max_workers=4) as workers:
    list(workers.map(cache, index["assets"]))
print("Cached", len(index["assets"]), "verified Webots R2025a dependencies")
