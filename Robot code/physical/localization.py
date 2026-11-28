"""Calibrated floor-marker pose + bounded IMU/encoder propagation (no HTTP)."""
import math
from protocol import wrap


class Localizer:
    def __init__(self, profile, cv2, np):
        self.p, self.cv, self.np = profile, cv2, np
        c=profile['camera']
        if c.get('validated') is not True:
            raise ValueError('Physical camera intrinsics/mounting are not validated')
        self.k=np.array(c['cameraMatrix'],dtype=float)
        self.d=np.array(c['distortionCoefficients'],dtype=float)
        self.mount=np.array(c['robotFromOpticalRotation'],dtype=float)
        self.offset=np.array(c['robotFromOpticalTranslation'],dtype=float).reshape(3,1)
        if (self.k.shape!=(3,3) or self.mount.shape!=(3,3) or not np.isfinite(self.k).all() or
            not np.isfinite(self.d).all() or not np.isfinite(self.offset).all() or
            not np.allclose(self.mount.T@self.mount,np.eye(3),atol=1e-5) or
            not np.isclose(np.linalg.det(self.mount),1,atol=1e-5)):
            raise ValueError('Invalid measured camera installation')
        self.anchor=None
        self.markers={}
        self.streak={}

    def vision(self, corners, ids, width, height, sensors, now):
        np,cv=self.np,self.cv
        if [width,height]!=self.p['camera']['imageSize']:
            raise ValueError('Camera resolution changed')
        known={m['arucoId']:m for m in self.p['markers']}
        observed={}
        candidates=[]
        for points, ident in zip(corners,ids):
            ident=int(ident)
            if ident not in known:
                continue
            marker=known[ident]
            points=np.asarray(points,dtype=float).reshape(4,2)
            if not np.isfinite(points).all() or min(np.linalg.norm(points[(i+1)%4]-points[i]) for i in range(4))<14:
                continue
            size=marker['markerSizeM']/2
            model=np.array([[-size,size,0],[size,size,0],[size,-size,0],[-size,-size,0]],dtype=float)
            yaw=math.radians(marker['markerYawDeg'])
            map_from_marker=np.array([[math.cos(yaw),-math.sin(yaw),0],[math.sin(yaw),math.cos(yaw),0],[0,0,1]])
            marker_position=np.array([[-marker['row']*self.p['metersPerGridCell']],[-marker['col']*self.p['metersPerGridCell']],[0]])
            options=[]
            for solver in (cv.SOLVEPNP_IPPE_SQUARE,cv.SOLVEPNP_SQPNP):
                solution=cv.solvePnPGeneric(model,points,self.k,self.d,flags=solver)
                for r,t in zip(solution[1],solution[2]):
                    if not np.isfinite(r).all() or not np.isfinite(t).all() or t[2,0]<=0:
                        continue
                    rot=map_from_marker@cv.Rodrigues(r)[0].T@self.mount.T
                    if rot[2,2]<math.cos(math.radians(15)):
                        continue
                    projected=cv.projectPoints(model,r,t,self.k,self.d)[0].reshape(4,2)
                    error=float(np.sqrt(np.mean(np.sum((projected-points)**2,axis=1))))
                    if error>2:
                        continue
                    camera_position=marker_position-map_from_marker@cv.Rodrigues(r)[0].T@t
                    position=camera_position-rot@self.offset
                    options.append(dict(x=float(position[0,0]),y=float(position[1,0]),
                        headingDeg=wrap(math.degrees(math.atan2(rot[1,0],rot[0,0]))),reprojectionErrorPx=error,
                        distanceM=float(np.linalg.norm((position-marker_position)[:2])),t=now))
            if not options:
                continue
            options.sort(key=lambda x:x['reprojectionErrorPx'])
            best=options[0]
            if any(x['reprojectionErrorPx']<best['reprojectionErrorPx']+.3 and abs(wrap(x['headingDeg']-best['headingDeg']))>8 for x in options[1:]):
                continue
            old=self.streak.get(ident)
            stable=(old and now-old['t']<.25 and abs(wrap(old['headingDeg']-best['headingDeg']))<3 and
                    math.hypot(old['x']-best['x'],old['y']-best['y'])<.08)
            best['stableFrames']=old['stableFrames']+1 if stable else 1
            observed[ident]=best
            if best['stableFrames']>=5:
                candidates.append(best)
        self.markers=observed
        self.streak=observed
        if candidates:
            candidates.sort(key=lambda x:x['reprojectionErrorPx'])
            best=candidates[0]
            if any(math.hypot(x['x']-best['x'],x['y']-best['y'])>.10 or abs(wrap(x['headingDeg']-best['headingDeg']))>8 for x in candidates[1:]):
                self.anchor=None
                return
            self.anchor=dict(best,odom=dict(sensors['odom']),imuYaw=sensors['imu']['yaw'])

    def pose(self, sensors, now):
        a=self.anchor
        if not a or not 0<=now-a['t']<=self.p['maxVisionGapSeconds']:
            return None
        o=sensors['odom']
        yaw=wrap(a['headingDeg']+math.degrees(sensors['imu']['yaw']-a['imuYaw']))
        # Align local ROS odom axes to map axes at the measured camera anchor.
        angle=math.radians(a['headingDeg'])-a['odom']['yaw']
        dx,dy=o['x']-a['odom']['x'],o['y']-a['odom']['y']
        if math.hypot(dx,dy)>self.p['maxOdometryGapM']:
            return None
        return dict(x=a['x']+math.cos(angle)*dx-math.sin(angle)*dy,
                    y=a['y']+math.sin(angle)*dx+math.cos(angle)*dy,headingDeg=yaw)
