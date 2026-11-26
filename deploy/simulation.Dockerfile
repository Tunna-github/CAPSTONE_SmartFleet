FROM cyberbotics/webots:R2025a-ubuntu22.04
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3-pip xauth && rm -rf /var/lib/apt/lists/*
COPY deploy/simulation-requirements.txt /tmp/requirements.txt
RUN python3 -m pip install --no-cache-dir -r /tmp/requirements.txt
COPY tools/cache_webots_assets.py /opt/smartfleet/cache_webots_assets.py
COPY simulation/config/webots-assets.json /tmp/webots-assets.json
RUN python3 /opt/smartfleet/cache_webots_assets.py /tmp/webots-assets.json /home/smartfleet/.cache/Cyberbotics/Webots/assets
WORKDIR /opt/smartfleet/project
COPY simulation/ ./
COPY Webots_Full_Mission_Controller/ ./controllers/my_controller_testing_fleet/
COPY tools/validate_simulation.py /opt/smartfleet/validate_simulation.py
COPY deploy/run-webots.sh /opt/smartfleet/run-webots.sh
RUN python3 /opt/smartfleet/validate_simulation.py /opt/smartfleet/project && python3 /opt/smartfleet/validate_simulation.py /opt/smartfleet/project config/dual-lane-manifest.json && chmod +x /opt/smartfleet/run-webots.sh
RUN useradd --uid 1000 --home-dir /home/smartfleet --no-create-home smartfleet && mkdir -p /state && chown -R smartfleet:smartfleet /home/smartfleet /state /opt/smartfleet/project
ENV HOME=/home/smartfleet LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null
USER smartfleet
EXPOSE 1234
ENTRYPOINT ["/opt/smartfleet/run-webots.sh"]
