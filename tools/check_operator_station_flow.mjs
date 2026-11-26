// Real Operator-only task creation. --create requires a separate test installation.
import { chromium } from '../FrontEnd for SmartFleet/node_modules/playwright/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve(import.meta.dirname,'..');
const env=Object.fromEntries(fs.readFileSync(path.join(root,'.env'),'utf8').split(/\r?\n/).filter(l=>l.includes('=')).map(l=>{const n=l.indexOf('=');return [l.slice(0,n),l.slice(n+1)];}));
const base=process.argv[2]||'http://localhost:8080';
const create=process.argv.includes('--create');
const allQr=process.argv.includes('--all-qr');
assert.ok(!create || new URL(base).port==='8086','Task writes are restricted to the isolated acceptance installation on port 8086');
const output=process.env.SMARTFLEET_EVIDENCE_DIR||path.join(root,'test-results/station-operator');
fs.mkdirSync(output,{recursive:true});
const browser=await chromium.launch({channel:'msedge',headless:true});
const errors=[],created=[];
try {
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  page.on('pageerror',e=>errors.push(e.message));
  page.on('response',r=>{if(r.status()>=500)errors.push(`HTTP ${r.status()}: ${new URL(r.url()).pathname}`);});
  await page.goto(base+'/login');
  await page.getByLabel('USERNAME OR EMAIL',{exact:true}).fill('operator');
  await page.getByLabel('PASSWORD',{exact:true}).fill(env.DEMO_OPERATOR_PASSWORD);
  await page.getByRole('button',{name:'Sign In',exact:true}).click();
  await page.waitForURL(/\/dashboard$/);
  const token=await page.evaluate(()=>localStorage.getItem('smartfleet_access_token'));
  const headers={Authorization:'Bearer '+token};
  const catalog=await page.request.get(base+'/api/v1/warehouse-catalog',{headers}).then(r=>r.json());
  const pickup=catalog.stations.find(s=>s.code==='P1'),delivery=catalog.stations.find(s=>s.code==='D1');
  assert.ok(pickup?.usable&&delivery?.usable,'Shipped demo stations must be ready for the Operator');
  assert.equal(pickup.arucoId,2);assert.equal(delivery.arucoId,7);
  if(allQr) {
    assert.equal(catalog.transportPoints.length,9);
    assert.deepEqual(catalog.transportPoints.filter(p=>p.pickupEligible&&p.deliveryEligible).map(p=>p.arucoId),[0,2,3,4,5,6,7,8]);
    const intersection=catalog.transportPoints.find(p=>p.arucoId===1);
    assert.ok(!intersection.pickupEligible&&!intersection.deliveryEligible);
  }
  await page.getByRole('button',{name:'Transport tasks',exact:true}).click();
  const scenarios=allQr&&create ? [[0,8,'MANUAL'],[7,2,'MANUAL'],[3,5,'MANUAL'],[4,6,'MANUAL'],[8,0,'AUTO']] :
    (create?['MANUAL','AUTO']:['MANUAL']).map(mode=>[2,7,mode]);
  for(const [pickupQr,deliveryQr,mode] of scenarios) {
    const selectedPickup=catalog.stations.find(s=>s.arucoId===pickupQr),selectedDelivery=catalog.stations.find(s=>s.arucoId===deliveryQr);
    await page.getByRole('button',{name:'Create Transport Task',exact:true}).click();
    await page.getByLabel('Pickup station',{exact:true}).selectOption(String(selectedPickup.stationId));
    await page.getByLabel('Delivery station',{exact:true}).selectOption(String(selectedDelivery.stationId));
    await page.getByLabel('Weight(kg) *',{exact:true}).fill('1');
    const resolved=await page.getByText(/Navigation resolved by station:/).innerText();
    assert.ok(resolved.includes(`QR${pickupQr} → QR${deliveryQr}`));
    if(mode==='AUTO') await page.locator('input[name="dispatchMode"][value="AUTO"]').check();
    await page.waitForTimeout(350); // Capture after the dialog/theme transition finishes.
    await page.screenshot({path:path.join(output,`operator-create-${mode.toLowerCase()}-${pickupQr}-${deliveryQr}.png`),fullPage:true});
    if(allQr) {
      assert.equal(await page.getByLabel('Pickup station',{exact:true}).locator('option').count(),10);
      assert.equal(await page.getByLabel('Pickup station',{exact:true}).locator('option[value="blocked-1"]').isDisabled(),true);
    }
    if(!create) {await page.getByRole('button',{name:'Cancel',exact:true}).click();break;}
    const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/v1/transport-tasks'&&r.request().method()==='POST');
    await page.getByRole('button',{name:mode==='AUTO'?'Create & Auto Assign':'Create Task (Manual)',exact:true}).click();
    const saved=await response;assert.equal(saved.status(),201,await saved.text());
    const task=await saved.json();
    assert.equal(task.taskStatus,'QUEUED'); // No simulator agents run in this isolated acceptance installation.
    const route=await page.request.get(base+`/api/v1/demo-routing/tasks/${task.taskId}/qr`,{headers}).then(r=>r.json());
    assert.equal(route.pickupArUcoId,pickupQr);assert.equal(route.deliveryArUcoId,deliveryQr);
    created.push({taskId:task.taskId,mode,status:task.taskStatus,route});
    await page.getByRole('button',{name:'Create Transport Task',exact:true}).waitFor();
  }
  const denied=await page.request.put(base+`/api/v1/warehouse-catalog/stations/${pickup.stationId}`,{headers,data:{}});
  assert.equal(denied.status(),403,'Operator must not gain station-configuration permissions');
  assert.deepEqual(errors,[]);
  const result={operatorOnlyLogin:true,stations:{P1:2,D1:7},allMapQrShown:allQr,intersectionTransitOnly:allQr,created,operatorCannotConfigureStations:true,errors};
  fs.writeFileSync(path.join(output,'operator-station-flow.json'),JSON.stringify(result,null,2));
  console.log(JSON.stringify(result,null,2));
} finally {await browser.close();}
