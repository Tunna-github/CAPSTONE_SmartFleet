
import subprocess
import json
import time
import paho.mqtt.client as mqtt

from IPython.display import clear_output
from datetime import datetime

# ========== CONFIGURATION ==========

ROBOT_CODE = "R01"

MQTT_HOST = "192.168.137.1"
MQTT_PORT = 1883

MQTT_TOPIC = "smartfleet/robot/{}/lidar".format(
    ROBOT_CODE
)

# ========== MQTT CONNECTION ==========

mqtt_client = mqtt.Client(
    client_id="JetRacer-LiDAR-{}".format(ROBOT_CODE)
)

mqtt_client.reconnect_delay_set(
    min_delay=1,
    max_delay=10
)

mqtt_client.connect_async(
    MQTT_HOST,
    MQTT_PORT,
    keepalive=30
)

mqtt_client.loop_start()


# ========== START ROS LIDAR READER ==========

command = (
    "source /opt/ros/melodic/setup.bash && "
    "source /home/jetson/catkin_ws/devel/setup.bash && "
    "exec python2 -u /home/jetson/lidar_scan_bridge.py"
)

lidar_process = subprocess.Popen(
    ["bash", "-c", command],
    stdout=subprocess.PIPE,
    stderr=subprocess.STDOUT,
    universal_newlines=True,
    bufsize=1
)


# ========== LIVE LIDAR MONITOR ==========

try:
    for line in lidar_process.stdout:

        line = line.strip()

        if not line.startswith("{"):
            continue

        try:
            payload = json.loads(line)
        except ValueError:
            continue

        status = payload.get("status", "NO_SCAN_DATA")
        distance = payload.get("distanceCm")

        # ========== PUBLISH MQTT ==========

        mqtt_status = "DISCONNECTED"

        if mqtt_client.is_connected():

            result = mqtt_client.publish(
                MQTT_TOPIC,
                json.dumps(payload),
                qos=0,
                retain=False
            )

            if result.rc == mqtt.MQTT_ERR_SUCCESS:
                mqtt_status = "QUEUED"
            else:
                mqtt_status = "PUBLISH ERROR"

        # ========== DISPLAY ==========

        timestamp = datetime.now().strftime(
            "%H:%M:%S"
        )

        clear_output(wait=True)

        print("====================================")
        print("       SMARTFLEET LIDAR MONITOR")
        print("====================================")

        print("Robot ID       : {}".format(ROBOT_CODE))
        print("Front Angle    : 100 degrees")
        print("Detection Range: 50 cm")
        print("------------------------------------")

        print("Obstacle       : {}".format(status))

        if distance is not None:
            print("Distance       : {:.1f} cm".format(distance))
        else:
            print("Distance       : N/A")

        print("------------------------------------")
        print("MQTT           : {}".format(mqtt_status))
        print("Topic          : {}".format(MQTT_TOPIC))
        print("Last Update    : {}".format(timestamp))
        print("Update Interval: 1 second")
        print("====================================")

except KeyboardInterrupt:
    print("\nLiDAR monitoring stopped.")

except Exception as e:
    print("\nERROR:", e)

finally:

    if lidar_process.poll() is None:
        lidar_process.terminate()

    try:
        lidar_process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        lidar_process.kill()
        lidar_process.wait()

    mqtt_client.disconnect()
    mqtt_client.loop_stop()

    print("Monitoring finished.")
