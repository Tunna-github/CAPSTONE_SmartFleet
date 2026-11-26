"""Laser adapter contract shared with backend RobotLidarEvent validation."""
import math


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
