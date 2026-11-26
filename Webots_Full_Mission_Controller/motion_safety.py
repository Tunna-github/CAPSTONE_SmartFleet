"""Laser adapter contract shared with backend RobotLidarEvent validation."""
import math


def measured_motion(previous, current, wheel_radius=.033):
    """Encoder speed in simulation time plus wall-clock rendering throughput."""
    if previous is None:return {}
    dt_sim=current[0]-previous[0];dt_wall=current[1]-previous[1]
    if dt_sim<=0 or dt_wall<=0:return {}
    left=(current[2]-previous[2])/dt_sim;right=(current[3]-previous[3])/dt_sim
    return {'leftMeasuredRadPerSimSecond':left,'rightMeasuredRadPerSimSecond':right,
            'simulationRealtimeFactor':dt_sim/dt_wall,
            'linearMetersPerSimSecond':(left+right)*.5*wheel_radius,
            'linearMetersPerWallSecond':(left+right)*.5*wheel_radius*dt_sim/dt_wall}


def front_lidar_report(ranges, fov, mounting_yaw=-math.pi/12):
    if not ranges or any(math.isnan(v) or v < 0 for v in ranges):
        return {'status':'NO_SCAN_DATA','distanceCm':None}
    cone=[v for i,v in enumerate(ranges) if abs((fov*(.5-i/max(1,len(ranges)-1))+mounting_yaw+math.pi)%(2*math.pi)-math.pi)<=math.radians(35)]
    if not cone:return {'status':'NO_SCAN_DATA','distanceCm':None}
    distance=min(cone)
    # CLEAR has no obstacle distance in the existing backend contract. A
    # finite wall 3m away is CLEAR, not a malformed CLEAR(distance=300).
    if distance>.50:return {'status':'CLEAR','distanceCm':None}
    return {'status':'CRITICAL' if distance<=.25 else 'DETECTED','distanceCm':distance*100}
