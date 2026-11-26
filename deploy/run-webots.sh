#!/bin/sh
set -eu
python3 /opt/smartfleet/validate_simulation.py /opt/smartfleet/project "${SMARTFLEET_SCENE_MANIFEST:-config/scene-manifest.json}"
exec xvfb-run -a -s '-screen 0 1280x720x24' webots --batch --stdout --stderr --mode=realtime --no-rendering --stream=w3d --port=1234 "/opt/smartfleet/project/${SMARTFLEET_WORLD:-worlds/smartfleet.wbt}"
