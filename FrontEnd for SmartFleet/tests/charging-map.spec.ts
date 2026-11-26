import {test,expect,type Page} from "@playwright/test";
import {readFileSync} from "node:fs";

async function setup(page:Page,role="Administrator") {
  const user={userId:1,fullName:"Charging test",roles:[role]};
  const map=JSON.parse(readFileSync(new URL('../../simulation/config/dual-lane-map.json',import.meta.url),'utf8'));
  map.revision=8;
  const state={writes:[] as any[],pauses:0,templates:0};
  await page.addInitScript(user=>{localStorage.setItem('smartfleet_access_token','test-only');localStorage.setItem('smartfleet_user',JSON.stringify(user));},user);
  await page.route('https://localhost:7166/**',async route=>{
    const p=new URL(route.request().url()).pathname;
    if(p.endsWith('/users/me'))return route.fulfill({json:user});
    if(p==='/api/v1/navigation/map') {
      if(route.request().method()==='PUT'){state.writes.push(route.request().postDataJSON());Object.assign(map,state.writes.at(-1),{revision:map.revision+1});}
      return route.fulfill({json:map});
    }
    if(p==='/api/v1/simulation/map-template') {state.templates++;return route.fulfill({json:map});}
    if(p==='/api/v1/simulation')return route.fulfill({json:{enabled:true,mapCompatible:true,streamReady:true}});
    if(p==='/api/v1/warehouse-catalog')return route.fulfill({json:{warehouses:[],zones:[],stations:[],robots:[],navigationWarehouseId:1,mapRevision:map.revision}});
    if(p==='/api/v1/demo-routing/map/validation')return route.fulfill({json:{qrNodeCount:71,corridorCount:109,readyForRouting:true,issues:[]}});
    if(p==='/api/v1/charging')return route.fulfill({json:{enabled:true,jobs:[{id:'job-1',robotCode:'R01',state:'RETURNING',mapRevision:8,stationId:10,batteryPercent:85,nextWaypointIndex:2,sourceMissionId:1,routeJson:null}]}});
    if(p.endsWith('/charging/job-1/pause')) {state.pauses++;return route.fulfill({json:{}});}
    return route.fulfill({json:p.endsWith('/positions')?{}:[]});
  });
  return state;
}

test('Editing a lane map preserves ordered links, dictionary and 71 commissioned markers',async({page})=>{
  const state=await setup(page);await page.goto('/admin/maps');
  await expect(page.getByText('Saved map revision: 8',{exact:false})).toBeVisible();
  await page.getByRole('button',{name:'Select QR0',exact:true}).click();
  await page.getByLabel('Display name',{exact:true}).fill('Transfer bay zero');
  await page.getByRole('button',{name:'Save to Database',exact:true}).click();
  await expect.poll(()=>state.writes.length).toBe(1);
  expect(state.writes[0].directedLinks).toBe(true);expect(state.writes[0].markerDictionary).toBe('DICT_4X4_250');
  expect(state.writes[0].links).toHaveLength(109);expect(state.writes[0].cells.flat().filter((c:any)=>c.type==='qr')).toHaveLength(71);
});

test('One-way link removal preserves the reverse bay spur; scene template stays a draft until Save',async({page})=>{
  const state=await setup(page);await page.goto('/admin/maps');
  await page.getByRole('button',{name:'Edit Links',exact:true}).click();
  await page.getByRole('button',{name:'Remove link QR0 to QR45',exact:true}).click();
  await expect(page.getByRole('button',{name:'Remove link QR45 to QR0',exact:true})).toBeVisible();
  await page.getByRole('button',{name:'Load commissioned scene template',exact:true}).click();
  await expect.poll(()=>state.templates).toBe(1);expect(state.writes).toHaveLength(0);
  await expect(page.getByText('Commissioned scene loaded as a draft.',{exact:false})).toBeVisible();
  await page.getByRole('button',{name:'Save to Database',exact:true}).click();
  await expect.poll(()=>state.writes.length).toBe(1);expect(state.writes[0].links).toHaveLength(109);
});

test('Operator sees charging safety controls in live tracking and the global Pause panel',async({page})=>{
  const state=await setup(page,'Warehouse Operator');await page.goto('/simulation');
  const charging=page.getByRole('region',{name:'Charging and return to service'});
  await expect(charging).toContainText('RETURNING');await expect(charging).toContainText('20%');
  await charging.getByRole('button',{name:'Pause robot',exact:true}).click();
  await expect.poll(()=>state.pauses).toBe(1);
  await expect(page.getByRole('button',{name:'Resume safe return'})).toHaveCount(0);
  await page.getByRole('button',{name:'Robot safety / Pause',exact:true}).click();
  await expect(page.getByRole('dialog',{name:'Robot safety controls'}).getByRole('button',{name:'Pause robot',exact:true})).toBeVisible();
});
