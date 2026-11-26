// Requires `npm ci` in the frontend. Uses generated demo credentials locally.
import { chromium } from '../FrontEnd for SmartFleet/node_modules/playwright/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve(import.meta.dirname,'..');
const config=Object.fromEntries(fs.readFileSync(path.join(root,'.env'),'utf8').trim().split(/\r?\n/).map(l=>l.split('=')));
const base=process.argv[2]||`http://127.0.0.1:${config.WEB_PORT||8080}`;
const trackedRobot=process.argv.find(arg=>arg.startsWith('--robot-code='))?.split('=')[1]||'R01';
const output=process.env.SMARTFLEET_EVIDENCE_DIR||path.join(root,'test-results','simulation');
fs.mkdirSync(output,{recursive:true});
const login=await fetch(base+'/api/v1/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},
  body:JSON.stringify({usernameOrEmail:'operator',password:config.DEMO_OPERATOR_PASSWORD})});
if(!login.ok) throw Error('Demo login failed: '+login.status);
const session=await login.json();
const browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-webgl','--ignore-gpu-blocklist']});
const errors=[],failed=[],sockets=[];
let sessionRequests=0;
try {
  const context=await browser.newContext({viewport:{width:1440,height:1000}});
  await context.addInitScript(session=>{
    localStorage.setItem('smartfleet_access_token',session.accessToken);
    localStorage.setItem('smartfleet_user',JSON.stringify(session.user));
  },session);
  const page=await context.newPage();
  page.on('request',r=>{if(r.method()==='POST'&&new URL(r.url()).pathname==='/api/v1/simulation/session')sessionRequests++;});
  page.on('pageerror',e=>errors.push(e.stack||e.message));
  page.on('console',m=>{if(m.type()==='error')errors.push(m.text().slice(0,300));});
  page.on('response',r=>{if(r.status()>=400){const failure={url:r.url().split('?')[0],status:r.status()};failed.push(failure);console.log('HTTP failure',JSON.stringify(failure));}});
  page.on('websocket',ws=>{
    const s={url:ws.url().split('?')[0],received:0,sent:[]};sockets.push(s);
    ws.on('framereceived',()=>s.received++);ws.on('framesent',f=>s.sent.push(String(f.payload).slice(0,100)));
    ws.on('socketerror',e=>errors.push(String(e)));
  });
  await page.goto(base+'/simulation');
  await page.getByRole('button',{name:'Fleet map 2D',exact:true}).click();
  await page.locator(`[data-robot-code="${trackedRobot}"]`).waitFor();
  await page.screenshot({path:path.join(output,'map-2d.png'),fullPage:true});
  let motion;
  if(process.argv.includes('--motion')) {
    const marker=page.locator(`[data-robot-code="${trackedRobot}"] rect`);
    const position=()=>marker.evaluate(r=>({x:Number(r.getAttribute('x')),y:Number(r.getAttribute('y'))}));
    const start=await position();
    for(let i=0;i<60;i++) {
      await page.waitForTimeout(1000);const current=await position();
      if(Math.hypot(current.x-start.x,current.y-start.y)>2) {motion={start,current};break;}
    }
    assert.ok(motion,'Map robot marker must move with the measured Webots position');
    const status=await page.getByLabel('Live robot status').innerText();
    assert.ok(status.includes('Battery 85%'));
    await page.screenshot({path:path.join(output,'map-2d-moving.png'),fullPage:true});
  }
  await page.getByRole('button',{name:'Webots 3D',exact:true}).click();
  await page.getByRole('button',{name:'Connect 3D',exact:true}).click();
  const frame=page.frameLocator('iframe[title="Live Webots 3D simulation"]');
  await frame.locator('canvas').first().waitFor({timeout:90000});
  await page.waitForTimeout(20000);
  await page.screenshot({path:path.join(output,'webots-3d.png'),fullPage:true,timeout:60000});
  const state=await frame.locator('body').evaluate(body=>({text:body.innerText.slice(0,500),
    canvases:[...body.querySelectorAll('canvas')].map(c=>({width:c.width,height:c.height})),
    shadow:body.querySelector('webots-view')?.shadowRoot?.innerHTML.slice(0,200)}));
  assert.equal(state.text,'','Viewer has not finished loading');
  assert.ok(state.canvases.some(c=>c.width>0&&c.height>0));
  const viewer=page.locator('iframe[title="Live Webots 3D simulation"]');
  await viewer.evaluate(el=>window.smartfleetKeptViewer=el);
  const firstSrc=await viewer.getAttribute('src');
  await page.getByRole('button',{name:'Fleet map 2D',exact:true}).click();
  await expectHidden(viewer);
  await page.getByRole('button',{name:'Transport tasks',exact:true}).click();
  await page.getByRole('heading',{name:'Transport Task Management',exact:true}).waitFor();
  assert.equal(await viewer.count(),1,'Internal navigation must retain the viewer');
  await page.getByRole('button',{name:'Live tracking & handover',exact:true}).click();
  await page.getByRole('button',{name:'Webots 3D',exact:true}).click();
  await viewer.waitFor({state:'visible'});
  assert.equal(await viewer.getAttribute('src'),firstSrc);
  assert.ok(await viewer.evaluate(el=>el===window.smartfleetKeptViewer));
  assert.equal(sessionRequests,1,'View/route switches must not create a new 3D session');
  const incoming=sockets.filter(s=>s.url.includes('/simulation/view')).reduce((n,s)=>n+s.received,0);
  await page.waitForTimeout(1500);
  assert.ok(sockets.filter(s=>s.url.includes('/simulation/view')).reduce((n,s)=>n+s.received,0)>incoming,'Retained 3D socket must still receive updates');
  await page.getByRole('button',{name:'Fleet map 2D',exact:true}).click();
  await page.getByRole('button',{name:'Switch to Light',exact:true}).click();
  await page.waitForTimeout(350);
  await page.screenshot({path:path.join(output,'operations-light.png'),fullPage:true});
  await page.getByRole('button',{name:'Switch to Dark',exact:true}).click();
  await page.getByRole('button',{name:'Webots 3D',exact:true}).click();
  // A second independent browser session observes the same server scene.
  const second=await browser.newContext({viewport:{width:1280,height:900}});
  await second.addInitScript(session=>{
    localStorage.setItem('smartfleet_access_token',session.accessToken);
    localStorage.setItem('smartfleet_user',JSON.stringify(session.user));
  },session);
  const secondPage=await second.newPage();
  await secondPage.goto(base+'/simulation');
  await secondPage.getByRole('button',{name:'Connect 3D',exact:true}).click();
  await secondPage.frameLocator('iframe').locator('canvas').first().waitFor({timeout:90000});
  const prior=await page.request.get(base+'/api/v1/simulation/poses',{headers:{Authorization:'Bearer '+session.accessToken}}).then(r=>r.json());
  const commands=await page.evaluate(async()=>new Promise(resolve=>{
    const ws=new WebSocket(location.origin.replace(/^http/,'ws')+'/api/v1/simulation/view/');
    let count=0;
    ws.onopen=()=>ws.send('w3d;broadcast');
    ws.onmessage=()=>{count++; if(count===1){ws.send('reset');ws.send('pause');ws.send('real-time:1');}};
    setTimeout(()=>{ws.close();resolve({received:count});},1500);
  }));
  const after=await page.request.get(base+'/api/v1/simulation/poses',{headers:{Authorization:'Bearer '+session.accessToken}}).then(r=>r.json());
  assert.ok(commands.received>5,'Viewer stream should continue after discarded controls');
  assert.equal(after.poses[0].runId,prior.poses[0].runId,'Browser must not reset world');
  assert.ok(after.poses[0].sequence>prior.poses[0].sequence,'Browser must not pause world');
  assert.deepEqual(errors,[]);assert.deepEqual(failed,[]);
  const result={state,errors,failed,sockets,independentViewers:2,controlCommandsDiscarded:true,persistentViewerAcrossNavigation:true,sessionRequests,motion};
  console.log(JSON.stringify(result,null,2));
  fs.writeFileSync(path.join(output,'browser-check.json'),JSON.stringify(result,null,2));
} finally {await browser.close();}

async function expectHidden(locator) {
  await locator.waitFor({state:'hidden'});
  assert.equal(await locator.count(),1,'Switching to 2D must hide, not destroy, the 3D iframe');
}
