// Observe-only regression: fail one browser's viewing socket, never the robot.
import { chromium } from '../FrontEnd for SmartFleet/node_modules/playwright/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';

const root=path.resolve(import.meta.dirname,'..');
const config=Object.fromEntries(fs.readFileSync(path.join(root,'.env'),'utf8').split(/\r?\n/)
  .filter(line=>line.includes('=')).map(line=>[line.slice(0,line.indexOf('=')),line.slice(line.indexOf('=')+1)]));
const base=process.argv[2]||`http://127.0.0.1:${config.WEB_PORT||8080}`;
const output=process.env.SMARTFLEET_EVIDENCE_DIR||path.join(root,'test-results','viewer-recovery');
fs.mkdirSync(output,{recursive:true});
const response=await fetch(base+'/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},
  body:JSON.stringify({usernameOrEmail:'operator',password:config.DEMO_OPERATOR_PASSWORD})});
assert.ok(response.ok,'Demo login failed');
const session=await response.json();
async function read(endpoint) {
  const r=await fetch(base+endpoint,{headers:{Authorization:'Bearer '+session.accessToken}});
  assert.ok(r.ok,`${endpoint}: ${r.status}`);return r.json();
}
const before=await read('/api/v1/simulation/poses');
const missionsBefore=await read('/api/v1/missions');
const browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-webgl','--ignore-gpu-blocklist']});
const dialogs=[],errors=[];
let sessions=0;
try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});
  await context.addInitScript(session=>{
    localStorage.setItem('smartfleet_access_token',session.accessToken);
    localStorage.setItem('smartfleet_user',JSON.stringify(session.user));
  },session);
  const page=await context.newPage();
  page.on('dialog',async d=>{dialogs.push(d.message());await d.dismiss();});
  page.on('pageerror',e=>errors.push(e.message));
  page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname==='/api/v1/simulation/session')sessions++;});
  await page.goto(base+'/simulation');
  await page.getByRole('button',{name:'Connect 3D',exact:true}).click();
  await page.getByText('3D connected',{exact:true}).waitFor({timeout:90000});
  const firstSrc=await page.locator('iframe').getAttribute('src');
  const frame=page.frameLocator('iframe');
  // Inject the same abnormal-close event R2025a receives at session expiration.
  // Only this viewer is destroyed; no simulator command or API write is used.
  await frame.locator('webots-view').evaluate(view=>{
    view._view.stream.socket.dispatchEvent(new CloseEvent('close',{code:1006}));
  });
  await page.waitForFunction(src=>{
    const frame=document.querySelector('iframe[title="Live Webots 3D simulation"]');
    return frame&&frame.getAttribute('src')!==src;
  },firstSrc,{timeout:15000});
  await page.getByText('3D connected',{exact:true}).waitFor({timeout:90000});
  assert.equal(sessions,2,'A fresh authorized viewing session must be created automatically');
  assert.deepEqual(dialogs,[],'R2025a must not open its reset dialog');
  assert.deepEqual(errors,[]);
  const after=await read('/api/v1/simulation/poses');
  assert.ok(after.poses.length>0);
  for(const pose of before.poses) {
    const current=after.poses.find(p=>p.robotCode===pose.robotCode);
    assert.equal(current?.runId,pose.runId,'Viewing recovery must not reset the simulator');
    assert.ok(current.sequence>pose.sequence,'The server simulation must keep advancing');
  }
  const missionsAfter=await read('/api/v1/missions');
  for(const mission of missionsBefore) {
    const current=missionsAfter.find(m=>m.missionId===mission.missionId);
    assert.ok(current,'Historical mission must remain present');
    assert.ok(current.nextWaypointIndex>=mission.nextWaypointIndex,'Viewing recovery must not rewind mission progress');
  }
  await page.screenshot({path:path.join(output,'reconnected-3d.png'),fullPage:true});
  const result={passed:true,injectedCloseCode:1006,dialogs,errors,authorizedSessions:sessions,
    sameSimulationRun:true,serverKeptAdvancing:true,missionProgressPreserved:true};
  fs.writeFileSync(path.join(output,'viewer-recovery.json'),JSON.stringify(result,null,2));
  console.log(JSON.stringify(result));
} finally {await browser.close();}
