"""Pure policy shared by Python 2 ROS bridge and offline tests."""
import math

MAX_SPEED = .18
MAX_STEER = .60
BROWSER_TIMEOUT = .35
SENSOR_TIMEOUT = .60
MAX_HOLD_SECONDS = 20.0


def finite(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and not (math.isnan(value) or math.isinf(value))


def valid_command(v, steer):
    return finite(v) and finite(steer) and abs(v) <= MAX_SPEED and abs(steer) <= MAX_STEER


def reason(now, last_command, sensors, topology_ok, moving_since, moving, max_hold=MAX_HOLD_SECONDS, require_lidar=True):
    if not topology_ok:
        return 'ROS_CONTROLLER_CONFLICT_OR_DRIVER_MISSING'
    if not 0 <= now - last_command <= BROWSER_TIMEOUT:
        return 'CONTROL_HEARTBEAT_TIMEOUT'
    for name in (('odom','imu','camera','lidar') if require_lidar else ('odom','imu','camera')):
        if name not in sensors or not 0 <= now - sensors[name] <= SENSOR_TIMEOUT:
            return 'STALE_' + name.upper()
    if moving and moving_since is not None and now - moving_since >= max_hold:
        return 'CONTINUOUS_MOTION_LIMIT'
    return None
