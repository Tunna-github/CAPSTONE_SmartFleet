import {test,expect,type Page} from '@playwright/test';

const warehouse={warehouseId:1,code:'W1',name:'Warehouse One',isActive:true};
const device={robotId:9,warehouseId:1,robotCode:'R09',robotType:'PHYSICAL',model:'JetRacer',controlSoftware:'ROS_AGENT',robotAgentVersion:null,ipAddress:null,macAddress:null,mqttClientIdentifier:'smartfleet-R09',maxPayloadWeightKg:10,isActive:true,registryRevision:3,connectionStatus:'OFFLINE',operationalStatus:'AVAILABLE'};
const report={window:{from:'2026-11-20',to:'2026-11-26',generatedAt:'2026-11-26T03:00:00Z',timeZone:'UTC+07',warehouseId:null,robotId:null},
    fleet:{registered:1,active:1,online:0,offline:1,available:0,assigned:0,maintenance:0,lowBattery:0,unknownBattery:1},
    tasks:{created:7,completed:3,failed:1,cancelled:1,other:2,completedInPeriod:4,liveQueue:2,liveExecuting:0,successRatePercent:75,averageCreateToAssignmentSeconds:null,averageCompletionSeconds:123,completionDurationSamples:4,statusCounts:{QUEUED:2,COMPLETED:3,FAILED:1,CANCELLED:1}},
    missions:{created:4,completed:3,failed:1,cancelled:0,failedWithActiveHold:1,averageExecutionSeconds:90,executionDurationSamples:3},
    maintenance:{created:1,completedInPeriod:0,openNow:1,incidentsCreated:1,unresolvedIncidentsNow:1},assignmentUtilizationPercent:10,
    robotUtilization:[{robotId:9,robotCode:'R09',warehouseId:1,isActive:true,assignments:4,completedMissions:3,assignedSeconds:3600,registeredSeconds:36000,utilizationPercent:10}],
    daily:[{date:'2026-11-26',createdTasks:7,completedTasks:4}],warehouses:[warehouse],robots:[{robotId:9,warehouseId:1,robotCode:'R09'}]};
async function setup(page:Page,role='Administrator',reject=false) {
    const user={userId:1,fullName:'Registry User',roles:[role]},writes:any[]=[],queries:string[]=[],exports:string[]=[];
    await page.addInitScript(user=>{localStorage.setItem('smartfleet_access_token','test-only');localStorage.setItem('smartfleet_user',JSON.stringify(user));},user);
    await page.route('https://localhost:7166/**',async route=>{
        const req=route.request(),url=new URL(req.url());
        if(url.pathname.endsWith('/users/me'))return route.fulfill({json:user});
        if(url.pathname==='/api/v1/robot-registry'&&req.method()==='GET')return route.fulfill({json:{robots:[device],warehouses:[warehouse],simulationRegistrationAllowed:false}});
        if(url.pathname.startsWith('/api/v1/robot-registry')&&req.method()!=='GET'){
            writes.push({url:url.pathname,body:req.postDataJSON()});return route.fulfill({status:reject?409:200,json:reject?{message:'Robot has an active assignment. Resolve it first.'}:{robotId:10}});
        }
        if(url.pathname==='/api/v1/analytics'){queries.push(url.search);return route.fulfill({json:report});}
        if(url.pathname.startsWith('/api/v1/analytics/export/')){exports.push(url.pathname+url.search);return route.fulfill({contentType:'application/octet-stream',body:'report bytes'});}
        if(url.pathname==='/api/v1/robots')return route.fulfill({json:[]});
        if(url.pathname==='/api/v1/navigation/map')return route.fulfill({json:{revision:1,rows:18,columns:28,cells:Array.from({length:18},()=>Array.from({length:28},()=>({type:'floor'}))),links:[]}});
        return route.fulfill({json:url.pathname.endsWith('/positions')?{}:[]});
    });return {writes,queries,exports};
}
test('Admin registers a device without editable runtime status or battery',async({page},info)=>{
    const {writes}=await setup(page);await page.goto('/admin/robots');await page.getByRole('button',{name:'Register robot',exact:true}).click();
    const form=page.getByRole('form',{name:'Robot registration editor'});await form.getByLabel('Robot code',{exact:true}).fill('R10');await form.getByLabel('Warehouse',{exact:true}).selectOption('1');await form.getByLabel('MQTT client identifier',{exact:true}).fill('smartfleet-R10');
    await expect(form.locator('input[name="connectionStatus"]')).toHaveCount(0);await expect(form.locator('input[name="batteryPercent"]')).toHaveCount(0);
    await expect(form.locator('select[name="robotType"] option[value="SIMULATION"]')).toHaveCount(0);
    await form.getByRole('button',{name:'Save robot',exact:true}).click();await expect(page.getByRole('status')).toContainText('configuration saved');
    expect(writes).toHaveLength(1);expect(writes[0].body).toMatchObject({warehouseId:1,robotCode:'R10',mqttClientIdentifier:'smartfleet-R10',maxPayloadWeightKg:10,registryRevision:0,isActive:true});
    expect(writes[0].body).not.toHaveProperty('connectionStatus');await page.screenshot({path:info.outputPath('robot-registry.png'),fullPage:true});
});
test('Unsafe configuration conflict retains editor and immutable identities',async({page})=>{
    const {writes}=await setup(page,'Administrator',true);await page.goto('/admin/robots');await page.getByRole('button',{name:'Configure R09',exact:true}).click();
    await expect(page.getByLabel('Robot code',{exact:true})).toHaveAttribute('readonly','');await page.getByLabel('Maximum payload (kg)').fill('12');await page.getByRole('button',{name:'Save robot',exact:true}).click();
    await expect(page.getByRole('alert')).toContainText('active assignment');await expect(page.getByRole('form',{name:'Robot registration editor'})).toBeVisible();expect(writes[0].body.registryRevision).toBe(3);
});
for(const role of ['Warehouse Operator','Maintenance Technician'])test(`${role} cannot open the Admin registry`,async({page})=>{
    const {writes}=await setup(page,role);await page.goto('/admin/robots');await expect(page.getByRole('heading',{name:'Robot registration & configuration',exact:true})).toHaveCount(0);expect(writes).toHaveLength(0);
});
for(const role of ['Administrator','Warehouse Operator'])test(`${role} sees database KPIs and report filters`,async({page},info)=>{
    const {queries,exports}=await setup(page,role);await page.goto('/dashboard/analytics');
    await expect(page.getByRole('heading',{name:'Dashboard analytics & reporting'})).toBeVisible();await expect(page.getByRole('region',{name:'Analytics KPIs'})).toContainText('75%');
    await expect(page.getByText('N/A',{exact:true})).toHaveCount(1);await expect(page.getByRole('cell',{name:'R09',exact:true})).toBeVisible();
    await page.getByLabel('Warehouse',{exact:true}).selectOption('1');await page.getByLabel('Robot',{exact:true}).selectOption('9');await page.getByRole('button',{name:'Apply filters',exact:true}).click();
    await expect.poll(()=>queries.some(q=>q.includes('warehouseId=1')&&q.includes('robotId=9'))).toBeTruthy();
    await expect(page.getByRole('button',{name:'Export Excel'})).toBeEnabled();const pending=page.waitForEvent('download');await page.getByRole('button',{name:'Export Excel'}).click();expect((await pending).suggestedFilename()).toMatch(/^smartfleet-.*\.xlsx$/);
    expect(exports[0]).toContain('warehouseId=1');expect(exports[0]).toContain('robotId=9');
    await page.getByText('How these metrics are calculated').click();await expect(page.getByText(/not measured motor motion/)).toBeVisible();await page.screenshot({path:info.outputPath(`analytics-${role}.png`),fullPage:true});
});
test('Invalid date order is rejected before requesting a report',async({page})=>{
    const {queries}=await setup(page);await page.goto('/dashboard/analytics');await expect(page.getByRole('region',{name:'Analytics KPIs'})).toBeVisible();const count=queries.length;
    await page.getByLabel('From',{exact:true}).fill('2026-11-26');await page.getByLabel('To',{exact:true}).fill('2026-11-20');await page.getByRole('button',{name:'Apply filters'}).click();await expect(page.getByRole('alert')).toContainText('ordered range');expect(queries).toHaveLength(count);
});
test('Maintenance cannot view analytics or export reports in UI',async({page})=>{
    const {queries}=await setup(page,'Maintenance Technician');await page.goto('/dashboard/analytics');await expect(page.getByRole('heading',{name:'Dashboard analytics & reporting'})).toHaveCount(0);expect(queries).toHaveLength(0);
});
