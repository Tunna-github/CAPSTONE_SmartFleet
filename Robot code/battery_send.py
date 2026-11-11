
from jetcard.ina219 import INA219
from IPython.display import clear_output
from datetime import datetime
import time
import json
import paho.mqtt.client as mqtt

# ========== CONFIGURATION ==========

ina = INA219(addr=0x41)

FULL_VOLTAGE = 12.6
EMPTY_VOLTAGE = 9.0

UPDATE_INTERVAL = 1  # Update every 1 second

# MQTT - SMARTFLEET BACKEND
SEND_MQTT = True

ROBOT_CODE = "R01"

# Replace with the IP address of your MQTT broker
MQTT_HOST = "192.168.137.1"
MQTT_PORT = 1883

MQTT_TOPIC = "smartfleet/robot/{}/telemetry".format(
    ROBOT_CODE
)

# ========== BATTERY FUNCTIONS ==========

def voltage_to_percent(bus_voltage):
    percent = (
        (bus_voltage - EMPTY_VOLTAGE) /
        (FULL_VOLTAGE - EMPTY_VOLTAGE)
    ) * 100

    return max(0, min(100, percent))


def read_battery():
    bus_voltage = ina.getBusVoltage_V()
    shunt_voltage = ina.getShuntVoltage_mV() / 1000.0

    battery_voltage = bus_voltage + shunt_voltage

    # Same calculation as your original code
    battery_percent = voltage_to_percent(bus_voltage)

    current = ina.getCurrent_mA()
    power = battery_voltage * current / 1000.0

    return battery_voltage, battery_percent, current, power


# ========== MQTT CONNECTION ==========

mqtt_client = None

if SEND_MQTT:
    mqtt_client = mqtt.Client(
        client_id="JetRacer-Battery-{}".format(ROBOT_CODE)
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


# ========== LIVE BATTERY MONITOR ==========

print("Starting JetRacer Battery Monitoring...")

try:
    while True:
        cycle_start = time.monotonic()

        voltage, percent, current, power = read_battery()

        timestamp = datetime.now().strftime("%H:%M:%S")

        # MQTT payload compatible with SmartFleet BE
        payload = {
            "batteryPercent": round(percent, 2),
            "batteryVoltage": round(voltage, 3)
        }

        mqtt_status = "DISABLED"

        # Send battery data to backend
        if SEND_MQTT:
            if mqtt_client.is_connected():
                result = mqtt_client.publish(
                    MQTT_TOPIC,
                    json.dumps(payload),
                    qos=0,
                    retain=False
                )

                if result.rc == mqtt.MQTT_ERR_SUCCESS:
                    mqtt_status = "SENT"
                else:
                    mqtt_status = "PUBLISH ERROR"
            else:
                mqtt_status = "DISCONNECTED"

        # Clear previous output, show only latest reading
        clear_output(wait=True)

        print("====================================")
        print("      JETRACER BATTERY MONITOR")
        print("====================================")
        print("Robot ID       : {}".format(ROBOT_CODE))
        print("Battery        : {:.1f}%".format(percent))
        print("Voltage        : {:.3f} V".format(voltage))
        print("Current        : {:.2f} mA".format(current))
        print("Power          : {:.3f} W".format(power))
        print("------------------------------------")
        print("MQTT           : {}".format(mqtt_status))
        print("Topic          : {}".format(MQTT_TOPIC))
        print("Last Update    : {}".format(timestamp))
        print("Update Interval: 1 second")
        print("====================================")

        # Maintain approximately 1 second per cycle
        time.sleep(max(
            0,
            UPDATE_INTERVAL -
            (time.monotonic() - cycle_start)
        ))

except KeyboardInterrupt:
    print("\nBattery monitoring stopped.")

except Exception as e:
    print("\nERROR:", e)

finally:
    if mqtt_client is not None:
        mqtt_client.disconnect()
        mqtt_client.loop_stop()

    print("Monitoring finished.")
