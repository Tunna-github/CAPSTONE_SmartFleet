// Read-only Docker UI regression: real Login and role-protected registry/analytics.
import {chromium} from '../FrontEnd for SmartFleet/node_modules/playwright/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
const root=path.resolve(import.meta.dirname,'..');
const env=Object.fromEntries(fs.readFileSync(path.join(root,'.env'),'utf8').split(/\r?\n/).filter(l=>l.includes('=')).map(l=>{const i=l.indexOf('=');return[l.slice(0,i),l.slice(i+1)];}));
const base=process.argv[2]||'http://127.0.0.1:8080';
const output=process.env.SMARTFLEET_EVIDENCE_DIR||path.join(root,'test-results/registry-browser');fs.mkdirSync(output,{recursive:true});
const browser=await chromium.launch({channel:'msedge',headless:true});
const errors=[],roles=[];
try {
    for(const role of ['admin','operator','maintenance']) {
        const context=await browser.newContext({viewport:{width:1440,height:1000}}),page=await context.newPage();
        page.on('pageerror',e=>errors.push(e.message));
        page.on('response',r=>{if(r.status()>=500)errors.push(`HTTP ${r.status()}: ${r.url().split('?')[0]}`);});
        await page.goto(base+'/login');
        await page.getByLabel('USERNAME OR EMAIL',{exact:true}).fill(role);
        if(role==='admin') {
            await page.getByLabel('PASSWORD',{exact:true}).fill('deliberately-wrong-password');
            const rejected=page.waitForResponse(r=>r.url().endsWith('/auth/login'));
            await page.getByRole('button',{name:'Sign In',exact:true}).click();assert.equal((await rejected).status(),401);
            assert.ok(page.url().endsWith('/login'));
        }
        await page.getByLabel('PASSWORD',{exact:true}).fill(env[`DEMO_${role.toUpperCase()}_PASSWORD`]);
        await page.getByRole('button',{name:'Sign In',exact:true}).click();
        await page.waitForURL(role==='admin'?/\/admin$/:role==='operator'?/\/dashboard$/:/\/dashboard\/robots$/);
        if(role==='admin') {
            await page.getByRole('button',{name:'Robot Registry',exact:true}).click();
            await page.getByRole('heading',{name:'Robot registration & configuration',exact:true}).waitFor();
            await page.getByRole('button',{name:'Register robot',exact:true}).click();
            await page.getByRole('form',{name:'Robot registration editor'}).waitFor();
            assert.equal(await page.locator('input[name="connectionStatus"]').count(),0);
            await page.screenshot({path:path.join(output,'registry-admin.png'),fullPage:true});
            await page.getByRole('button',{name:'Close editor',exact:true}).click();
        }
        if(role!=='maintenance') {
            await page.goto(base+'/dashboard/analytics');await page.getByRole('region',{name:'Analytics KPIs'}).waitFor();
            await page.getByRole('region',{name:'Task status totals'}).waitFor();
            const token=await page.evaluate(()=>localStorage.getItem('smartfleet_access_token'));
            const response=await page.request.get(base+'/api/v1/analytics',{headers:{Authorization:'Bearer '+token}});
            assert.equal(response.status(),200);const report=await response.json();
            const registered=page.locator('dl > div').filter({has:page.locator('dt',{hasText:/^Registered robots$/})});
            assert.equal(await registered.locator('dd').innerText(),String(report.fleet.registered));
            await page.screenshot({path:path.join(output,`analytics-${role}.png`),fullPage:true});
        } else {
            await page.goto(base+'/admin/robots');await page.waitForURL(/\/dashboard\/robots$/);
            await page.goto(base+'/dashboard/analytics');await page.waitForURL(/\/dashboard\/robots$/);
        }
        roles.push(role);await context.close();
    }
    assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'browser-check.json'),JSON.stringify({loginRoles:roles,invalidLoginRejected:true,registryAndAnalyticsChecked:true,pageErrors:errors},null,2));
    console.log('PASS: real Login for all three roles, invalid login rejected, Admin registry, Admin/Operator database analytics, Maintenance denied, no page/server errors.');
} finally {await browser.close();}
