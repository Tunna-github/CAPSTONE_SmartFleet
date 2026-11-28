"""Pixel-only line following and distinct-frame ArUco confirmation. No metric pose."""
import math


def is_line_demo(profile):
    return profile.get('navigationMode')=='MARKER_LINE_DEMO'


class MarkerLineVision:
    def __init__(self, profile, cv2, np):
        self.p,self.cv,self.np=profile,cv2,np
        self.markers={}
        self.line={}

    def vision(self, gray, corners, ids, now):
        previous=self.markers
        observed={}
        for points,ident in zip(corners,ids):
            ident=int(ident)
            old=previous.get(ident)
            streak=old['stableFrames']+1 if old and 0<=now-old['t']<=.25 else 1
            observed[ident]=dict(t=now,stableFrames=streak,
                areaPx=float(self.cv.contourArea(self.np.asarray(points,dtype=self.np.float32).reshape(4,2))))
        self.markers=observed
        cv,np=self.cv,self.np
        height,width=gray.shape[:2]
        top,bottom=self.p.get('lineRoi',[.2,.9])
        y0,y1=int(height*top),int(height*bottom)
        margin=int(width*.05)
        _,mask=cv.threshold(gray,self.p.get('lineBlackThreshold',85),255,cv.THRESH_BINARY_INV)
        # A printed marker is not the line. Exclude its polygon before steering.
        for points in corners:
            polygon=np.asarray(points,dtype=float).reshape(4,2)
            center=polygon.mean(axis=0)
            polygon=np.round(center+(polygon-center)*1.12).astype(np.int32)
            cv.fillConvexPoly(mask,polygon,0)
        roi=mask[y0:y1,margin:width-margin]
        found=cv.findContours(roi,cv.RETR_EXTERNAL,cv.CHAIN_APPROX_SIMPLE)
        contours=found[0] if len(found)==2 else found[1]
        candidates=[]
        for contour in contours:
            area=float(cv.contourArea(contour))
            x,y,w,h=cv.boundingRect(contour)
            if area<self.p.get('lineMinAreaPx',80) or w>.65*roi.shape[1] or h<8:
                continue
            moments=cv.moments(contour)
            if moments['m00']<=0:continue
            cx=moments['m10']/moments['m00']
            error=(cx-(roi.shape[1]-1)/2)/((roi.shape[1]-1)/2)
            candidates.append((area,error))
        if not candidates:
            self.line=dict(t=now,visible=False,error=None,steer=0)
            return
        area,error=max(candidates)
        steer=-self.p.get('lineKp',.6)*error
        limit=self.p.get('maxLineSteerRad',.2)
        self.line=dict(t=now,visible=True,error=error,areaPx=area,steer=max(-limit,min(limit,steer)))
