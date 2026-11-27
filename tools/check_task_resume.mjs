// Opt-in live test: actual motion -> Pause -> stopped ACK -> same-task Resume.
import { chromium } from "../FrontEnd for SmartFleet/node_modules/playwright/index.mjs";
import fs from "node:fs";
import path from "node:path";
import assert from "node:assert/strict";
if (!process.argv.includes("--run-demo-test")) throw Error("Use --run-demo-test on the idle simulation host");
const root=path.resolve(import.meta.dirname,"..");
const cfg=Object.fromEntries(fs.readFileSync(path.join(root,".env"),"utf8").trim().split(/\r?\n/).map(l=>{const n=l.indexOf("=");return[l.slice(0,n),l.slice(n+1)];}));
const base=`http://127.0.0.1:${cfg.WEB_PORT||8080}`;
const out=process.env.SMARTFLEET_EVIDENCE_DIR;if(!out)throw Error("Set private evidence directory");fs.mkdirSync(out,{recursive:true});
const login=await fetch(base+"/api/v1/auth/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({usernameOrEmail:"operator",password:cfg.DEMO_OPERATOR_PASSWORD})});
assert.equal(login.status,200);const session=await login.json();
const browser=await chromium.launch({channel:"msedge",headless:true});
try {
 const context=await browser.newContext({viewport:{width:1440,height:1050}});
 await context.addInitScript(s=>{localStorage.setItem("smartfleet_access_token",s.accessToken);localStorage.setItem("smartfleet_user",JSON.stringify(s.user));},session);
 const headers={Authorization:"Bearer "+session.accessToken};
 const get=async url=>{const r=await context.request.get(base+url,{headers});assert.ok(r.ok(),await r.text());return r.json();};
 assert.ok(!(await get('/api/v1/missions')).some(m=>m.assignmentStatus==='ACTIVE'),'Do not interrupt another task');
 const cat=await get('/api/v1/warehouse-catalog');const w=cat.navigationWarehouseId;
 assert.equal((await get('/api/v1/map-profiles')).activeKey,'map2');
 const stations=Object.fromEntries(cat.stations.filter(s=>s.warehouseId===w&&s.isActive&&s.type==='TRANSFER').map(s=>[s.arucoId,s]));
 const created=await context.request.post(base+'/api/v1/transport-tasks',{headers,data:{warehouseId:w,pickupStationId:stations[5].stationId,deliveryStationId:stations[7].stationId,payloadWeightKg:1,priorityLevel:2,dispatchMode:'MANUAL',itemDescription:'[Validation] Quick same-task Pause / Resume'}});
 assert.ok(created.ok(),await created.text());const task=await created.json();
 const pose=async()=>(await get('/api/v1/simulation/poses')).poses.find(p=>p.robotCode==='R02');
 const originalPose=await pose();
 const assigned=await context.request.post(base+`/api/v1/transport-tasks/${task.taskId}/assign`,{headers,data:{robotId:2}});assert.equal(assigned.status(),200,await assigned.text());
 const assignment=await assigned.json();console.log('Assigned quick test',task.taskId,assignment.missionId);
 async function waitUntil(check,seconds=35){const end=Date.now()+seconds*1000;while(Date.now()<end){const value=await check();if(value)return value;await new Promise(r=>setTimeout(r,500));}throw Error('Timed out waiting for test condition');}
 await waitUntil(async()=>{const p=await pose();return Math.hypot(p.x-originalPose.x,p.y-originalPose.y)>.08;});
 const page=await context.newPage();await page.goto(base+'/simulation');await page.getByRole('button',{name:'Robot safety / Pause',exact:true}).click();
 const panel=page.getByRole('dialog',{name:'Robot safety controls'});
 await panel.getByRole('textbox',{name:'Reason'}).fill('Quick test: inspect the clear path before same-task resume');
 const pauseResponse=page.waitForResponse(r=>r.url().endsWith(`/${task.taskId}/pause`)&&r.request().method()==='POST');
 await panel.getByRole('button',{name:'Pause / Stop',exact:true}).click();const stoppedResponse=await pauseResponse;assert.equal(stoppedResponse.status(),200,await stoppedResponse.text());
 const row=async()=>(await get('/api/v1/task-interventions')).find(r=>r.taskId===task.taskId);
 await waitUntil(async()=>{const r=await row();return r?.intervention?.paused&&r.intervention.commandAcknowledged&&r.intervention.resumeSupported;});
 const stopped=await pose();await new Promise(r=>setTimeout(r,1600));const still=await pose();assert.ok(Math.hypot(still.x-stopped.x,still.y-stopped.y)<.025,'Robot must stay stopped');
 const before=(await get('/api/v1/missions')).find(m=>m.missionId===assignment.missionId);
 await page.screenshot({path:path.join(out,'paused.png'),fullPage:true});
 await panel.getByRole('checkbox',{name:'I checked that the path, robot and current load are safe to continue.',exact:true}).check();
 const response=page.waitForResponse(r=>r.url().endsWith(`/${task.taskId}/resume`)&&r.request().method()==='POST');
 await panel.getByRole('button',{name:'Resume task',exact:true}).click();const resumed=await response;assert.equal(resumed.status(),200,await resumed.text());
 await waitUntil(async()=>{const r=await row();if(r?.intervention?.resumeFailureReason)throw Error(r.intervention.resumeFailureReason);return r?.intervention?.paused===false&&r.intervention.commandAcknowledged;});
 await waitUntil(async()=>{const p=await pose();return Math.hypot(p.x-still.x,p.y-still.y)>.08;});
 const after=(await get('/api/v1/missions')).find(m=>m.missionId===assignment.missionId);
 assert.equal(after.assignmentId,before.assignmentId);assert.equal(after.taskId,before.taskId);assert.equal(after.assignmentStatus,'ACTIVE');
 assert.equal(after.pickupVerified,before.pickupVerified);assert.equal(after.deliveryVerified,before.deliveryVerified);
 await page.screenshot({path:path.join(out,'resumed.png'),fullPage:true});
 const report={passed:true,taskId:task.taskId,missionId:assignment.missionId,assignmentId:assignment.assignmentId,
  actualMotionBeforePause:true,actualStoppedPoseVerified:true,robotStopAck:true,sameMissionResumed:true,
  actualMotionAfterResume:true,loadVerificationUnchanged:true,fullMissionAwaited:false};
 fs.writeFileSync(path.join(out,'report.json'),JSON.stringify(report,null,2));console.log(JSON.stringify(report));
} finally {await browser.close();}
