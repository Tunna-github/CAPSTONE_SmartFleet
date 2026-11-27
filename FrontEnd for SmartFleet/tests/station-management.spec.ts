import {test,expect,type Page} from "@playwright/test";

async function setup(page:Page,role="Administrator",reject=false) {
    const user={userId:1,fullName:"Station Admin",roles:[role]};const writes:{url:string;body:any}[]=[];
    const stations=[{stationId:1,zoneId:1,warehouseId:1,code:"P1",name:"Pickup One",type:"PICKUP",coordX:1,coordY:2,coordZ:0,yawAngle:0,qrCodePayload:"P1",arucoId:2,isActive:true,capacity:1,revision:3,usable:true,isOccupied:false,occupantRobotCodes:[],reservedRobotId:null},
        {stationId:2,zoneId:1,warehouseId:1,code:"D1",name:"Delivery One",type:"DELIVERY",coordX:2,coordY:3,coordZ:0,yawAngle:0,qrCodePayload:"D1",arucoId:7,isActive:true,capacity:1,revision:2,usable:true,isOccupied:false,occupantRobotCodes:[],reservedRobotId:null},
        {stationId:3,zoneId:1,warehouseId:1,code:"WAIT",name:"Waiting",type:"WAITING",coordX:0,coordY:0,coordZ:0,yawAngle:0,qrCodePayload:null,arucoId:8,isActive:true,capacity:1,revision:1,usable:true,isOccupied:true,occupantRobotCodes:["R02"],reservedRobotId:2}];
    const catalog={warehouses:[{warehouseId:1,code:"W1",name:"Warehouse One",isActive:true,revision:1}],zones:[{zoneId:1,warehouseId:1,code:"Z1",name:"Floor",type:"TRANSIT",speedLimit:.18,isActive:true,revision:1}],stations,
        robots:[{robotId:1,robotCode:"R01",warehouseId:1},{robotId:2,robotCode:"R02",warehouseId:1}],navigationWarehouseId:1,mapRevision:7,capacityPolicy:"One robot per marker; explicit reservation and physical clearance."};
    const cells:any[][]=Array.from({length:18},()=>Array.from({length:28},()=>({type:"floor"})));
    for(const [id,row,col] of [[0,5,4],[2,1,4],[7,3,6],[8,1,6],[1,3,4]])cells[row][col]={type:"qr",label:`Marker-${id}`,arucoId:id,markerYawDeg:0,markerSizeM:.04,isIntersection:id===1};
    cells[6][9]={type:"station",label:"S1"};
    cells[6][13]={type:"station",label:"S2"};
    cells[9][13]={type:"charge",label:"CHG1"};
    let links=[["Marker-0","Marker-1"],["Marker-1","Marker-2"],["Marker-2","Marker-8"],["Marker-8","Marker-7"],["S1","S2"],["S2","CHG1"]];
    let compatible=true;
    await page.addInitScript(user=>{localStorage.setItem("smartfleet_access_token","test-only");localStorage.setItem("smartfleet_user",JSON.stringify(user));},user);
    await page.route("https://localhost:7166/**",async route=>{
        const req=route.request(),url=new URL(req.url());
        if(url.pathname.endsWith('/users/me'))return route.fulfill({json:user});
        if(url.pathname==='/api/v1/simulation')return route.fulfill({json:{enabled:true,mapCompatible:compatible}});
        if(url.pathname==='/api/v1/warehouse-catalog')return route.fulfill({json:catalog});
        if(url.pathname.startsWith('/api/v1/warehouse-catalog/')){
            writes.push({url:url.pathname,body:req.postDataJSON()});
            if(reject)return route.fulfill({status:409,json:{message:"Warehouse has active assignments. Recover before catalog changes."}});
            if(req.method()==="PUT") {
                const station=stations.find(s=>s.stationId===Number(url.pathname.split("/").at(-1)));
                if(station) Object.assign(station,req.postDataJSON(),{revision:station.revision+1});
            } else if(req.method()==="POST" && url.pathname.endsWith("/stations")) {
                stations.push({stationId:4,warehouseId:1,usable:true,isOccupied:false,occupantRobotCodes:[],reservedRobotId:null,
                    ...req.postDataJSON(),revision:1});
            }
            return route.fulfill({json:{id:1}});
        }
        if(url.pathname==='/api/v1/navigation/map'){
            if(req.method()!=='GET') {
                const submitted=req.postDataJSON();
                writes.push({url:url.pathname,body:submitted});
                compatible=submitted.links.length===links.length && submitted.cells.flat().filter((point: {type:string})=>point.type==="qr").length===5;
                cells.splice(0,cells.length,...submitted.cells);
                links=submitted.links;
                catalog.mapRevision++;
            }
            return route.fulfill({json:{revision:catalog.mapRevision,rows:18,columns:28,cells,links}});
        }
        if(url.pathname==='/api/v1/demo-routing/map/validation')return route.fulfill({json:{readyForRouting:true,qrNodeCount:4,corridorCount:3,issues:[]}});
        return route.fulfill({json:url.pathname.endsWith('/positions')?{}:[]});
    });
    return {writes, stations, catalog};
}

test("Operator selects another warehouse with its own stations and sees the queue-only policy", async ({page}) => {
    const {catalog,stations}=await setup(page,"Warehouse Operator");
    catalog.warehouses.push({warehouseId:2,code:"WH1",name:"Warehouse 1 (Map 1)",isActive:true,revision:1});
    catalog.zones.push({zoneId:2,warehouseId:2,code:"STORAGE",name:"Other storage",type:"STORAGE",speedLimit:.18,isActive:true,revision:1});
    stations.push({...stations[0],stationId:17,zoneId:2,warehouseId:2,code:"WH1-QR2",name:"Milk WH1",type:"TRANSFER",qrCodePayload:"WH1:QR2"});
    stations.push({...stations[1],stationId:18,zoneId:2,warehouseId:2,code:"WH1-QR7",name:"Noodles WH1",type:"TRANSFER",qrCodePayload:"WH1:QR7"});
    await page.goto('/dashboard/tasks');await page.getByRole('button',{name:'Create Transport Task',exact:true}).click();
    await page.getByRole('combobox',{name:'Warehouse',exact:true}).selectOption('2');
    await expect(page.getByText(/This warehouse world is inactive/)).toBeVisible();
    const pickup=page.getByRole('combobox',{name:'Pickup station',exact:true});
    await expect(pickup.locator('option[value="1"]')).toHaveCount(0);
    await pickup.selectOption('17');await page.getByRole('combobox',{name:'Delivery station',exact:true}).selectOption('18');
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText('QR2 → QR7');
});

test("Admin edits station mapping without writing map geometry",async({page},info)=>{
    const {writes}=await setup(page);await page.goto('/warehouse-stations');
    await page.getByRole('button',{name:'Edit P1',exact:true}).click();
    await expect(page.locator('select[name="arucoId"]')).toHaveValue('2');
    await expect(page.locator('select[name="arucoId"] option[value="7"]')).toBeDisabled();
    await expect(page.locator('select[name="arucoId"] option[value="1"]')).toHaveCount(0);
    await page.getByLabel('Display name',{exact:true}).fill('Renamed logical pickup');
    await page.getByRole('button',{name:'Save station',exact:true}).click();
    await expect(page.getByRole('status')).toContainText('Saved to database');
    expect(writes).toHaveLength(1);expect(writes[0].body).toMatchObject({arucoId:2,zoneId:1,revision:3,name:'Renamed logical pickup',capacity:1});
    await page.screenshot({path:info.outputPath('station-catalog.png'),fullPage:true});
});

test("Active assignment conflict preserves station editor and displays backend reason",async({page})=>{
    await setup(page,'Administrator',true);await page.goto('/warehouse-stations');
    await page.getByRole('button',{name:'Edit P1',exact:true}).click();
    await page.getByRole('button',{name:'Save station',exact:true}).click();
    await expect(page.getByRole('status')).toContainText('active assignments');await expect(page.getByRole('form',{name:'Catalog editor'})).toBeVisible();
});

for(const role of ['Warehouse Operator','Maintenance Technician'])test(`${role} cannot edit warehouse configuration`,async({page})=>{
    await setup(page,role);await page.goto('/warehouse-stations');
    await expect(page.getByRole('heading',{name:'Warehouse & Station Management'})).toBeVisible();
    await expect(page.getByRole('button',{name:'Add station',exact:true})).toHaveCount(0);
    await expect(page.getByRole('button',{name:'Edit P1',exact:true})).toHaveCount(0);
    if(role==='Maintenance Technician')await expect(page.getByRole('button',{name:'Release berth',exact:true})).toBeDisabled();
    else await expect(page.getByRole('button',{name:'Reserve berth',exact:true})).toHaveCount(0);
});

test("Map labels show physical ArUco ID and associated database station",async({page},info)=>{
    await setup(page);await page.goto('/admin/maps');
    await expect(page.locator('svg text').filter({hasText:/^QR2$/})).toHaveCount(1);
    await expect(page.locator('svg text').filter({hasText:/^Pickup One$/})).toHaveCount(1);
    await expect(page.locator('svg text').filter({hasText:/^Pickup Station$/})).toHaveCount(1);
    await expect(page.locator('svg title').filter({hasText:'Pickup One'})).toHaveCount(1);
    await page.screenshot({path:info.outputPath('station-map-labels.png'),fullPage:true});
});

test("Simulation maps an existing station to a QR and reloads its label without saving geometry",async({page})=>{
    const {writes,stations}=await setup(page);
    Object.assign(stations[0],{arucoId:null,usable:false});
    await page.goto('/admin/maps');
    const map=page.getByLabel('Warehouse map',{exact:true});
    await expect(map.locator('polyline')).toHaveCount(4);
    await expect(map.getByText(/^(S1|S2|CHG1)$/)).toHaveCount(0);
    await expect(page.getByRole('button',{name:'Edit Points'})).toBeVisible();
    await expect(page.getByRole('button',{name:'Save to Database'})).toBeEnabled();
    await map.getByRole('button',{name:'Select QR1',exact:true}).click();
    await expect(page.getByText('Intersection checkpoints are reserved for traffic coordination.')).toBeVisible();
    await map.getByRole('button',{name:'Select QR0',exact:true}).click();
    await expect(page.getByRole('combobox',{name:'Associated station'})).toHaveValue('');
    await expect(page.getByRole('combobox',{name:'Associated station'}).locator('option[value="2"]')).toHaveCount(0);
    await page.getByRole('combobox',{name:'Associated station'}).selectOption('1');
    await page.getByLabel('Station name',{exact:true}).fill('Packing Bench');
    await page.getByRole('combobox',{name:'Station type',exact:true}).selectOption('WAITING');
    await page.getByRole('button',{name:'Save station',exact:true}).click();
    await expect(page.getByText('Station saved at QR0.')).toBeVisible();
    expect(writes).toEqual([{url:'/api/v1/warehouse-catalog/stations/1',body:{
        zoneId:1,code:'P1',name:'Packing Bench',type:'WAITING',coordX:1,coordY:2,coordZ:0,yawAngle:0,
        qrCodePayload:'P1',arucoId:0,isActive:true,capacity:1,revision:3,
    }}]);
    await page.reload();
    await expect(map.getByLabel('Packing Bench — Waiting Area')).toBeVisible();
    await expect(map.locator('polyline')).toHaveCount(4);
    await map.getByRole('button',{name:'Select QR2',exact:true}).click();
    await page.getByRole('combobox',{name:'Associated station'}).selectOption('new');
    await page.getByLabel('Station code',{exact:true}).fill('W2');
    await page.getByLabel('Station name',{exact:true}).fill('Spare waiting');
    await page.getByRole('combobox',{name:'Station type',exact:true}).selectOption('WAITING');
    await page.getByLabel('X (meters)',{exact:true}).fill('1.25');
    await page.getByLabel('Y (meters)',{exact:true}).fill('2.5');
    await page.getByRole('button',{name:'Save station',exact:true}).click();
    await expect(page.getByText('Station saved at QR2.')).toBeVisible();
    expect(writes[1]).toMatchObject({url:'/api/v1/warehouse-catalog/stations',body:{arucoId:2,code:'W2',type:'WAITING'}});
});

test("Simulation saves independent labels, edits QR topology, and shows the commissioning warning",async({page},info)=>{
    const {writes,stations}=await setup(page);
    stations.pop(); // QR8 is an unassociated checkpoint in this scenario.
    await page.goto('/admin/maps');
    const map=page.getByLabel('Warehouse map',{exact:true});
    await map.getByRole('button',{name:'Select QR8',exact:true}).click();
    await page.getByLabel('Display name',{exact:true}).fill('Waiting Area A');
    await page.getByRole('combobox',{name:'Operational role',exact:true}).selectOption('WAITING');
    await expect(page.getByLabel('ArUco ID',{exact:true})).toHaveValue('8');
    await page.getByRole('button',{name:'Save to Database',exact:true}).click();
    await expect(page.getByText('Saved map to database!',{exact:true})).toBeVisible();
    expect(writes[0].body.cells[1][6]).toMatchObject({label:'Marker-8',displayName:'Waiting Area A',arucoId:8,operationalRole:'WAITING'});
    expect(writes[0].body.links).toContainEqual(['Marker-2','Marker-8']);
    expect(writes[0].body.links).toContainEqual(['S1','S2']);
    await page.getByRole('button',{name:'Reload Backend Map'}).click();
    await expect(map.getByText('Waiting Area A',{exact:true})).toBeVisible();
    await expect(map.getByText(/^(S1|S2|CHG1)$/)).toHaveCount(0);
    await map.getByRole('button',{name:'Select QR8',exact:true}).click();
    await expect(page.getByRole('combobox',{name:'Associated station'})).toHaveValue('');
    await page.screenshot({path:info.outputPath('qr8-editor.png'),fullPage:true});
    await page.getByRole('button',{name:'Edit Links'}).click();
    await map.getByRole('button',{name:'Select QR2',exact:true}).click();
    await map.getByRole('button',{name:'Select QR8',exact:true}).click();
    await expect(map.locator('polyline')).toHaveCount(3);
    await page.getByRole('button',{name:'Save to Database',exact:true}).click();
    await expect(page.getByRole('alert').filter({hasText:'Simulation dispatch is blocked'})).toBeVisible();
    await map.getByRole('button',{name:'Select QR8',exact:true}).click();
    await map.getByRole('button',{name:'Select QR2',exact:true}).click();
    await expect(map.locator('polyline')).toHaveCount(4);
    await page.getByRole('button',{name:'Edit Points'}).click();
    await page.getByRole('button',{name:'Add QR checkpoint'}).click();
    await page.getByLabel('New ArUco ID').fill('12');
    await map.locator('[data-map-cell="5,5"]').click();
    await expect(page.getByRole('heading',{name:'QR12 checkpoint'})).toBeVisible();
    await page.getByLabel('Display name',{exact:true}).fill('Navigation test');
    await page.getByLabel('ArUco ID',{exact:true}).fill('13');
    await page.getByRole('combobox',{name:'Operational role',exact:true}).selectOption('INTERSECTION');
    await page.getByRole('button',{name:'Save to Database',exact:true}).click();
    await expect(page.getByText('Saved map to database!',{exact:true})).toBeVisible();
    expect(writes.at(-1)?.body.cells[5][5]).toMatchObject({label:'QR12',displayName:'Navigation test',arucoId:13,isIntersection:true});
    await page.getByRole('button',{name:'Remove checkpoint'}).click();
    await expect(map.getByRole('button',{name:'Select QR13',exact:true})).toHaveCount(0);
    await page.getByRole('button',{name:'Save to Database',exact:true}).click();
    await expect(page.getByText('Saved map to database!',{exact:true})).toBeVisible();
    expect(writes.at(-1)?.body.cells[5][5]).toMatchObject({type:'floor',arucoId:null});
    await page.getByRole('button',{name:'Reload Backend Map'}).click();
    await expect(map.getByRole('button',{name:'Select QR13',exact:true})).toHaveCount(0);
});

test("Task selects operational stations; WAITING and independent QR selectors are absent",async({page})=>{
    await setup(page,'Warehouse Operator');await page.goto('/dashboard/tasks');
    await page.getByRole('button',{name:'Create Transport Task',exact:true}).click();
    await page.getByLabel('Pickup station',{exact:true}).selectOption('1');await page.getByLabel('Delivery station',{exact:true}).selectOption('2');
    await expect(page.locator('select[name="pickupStationId"] option[value="3"]')).toHaveCount(0);
    await expect(page.locator('select[name="pickupArUcoId"]')).toHaveCount(0);
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText('QR2 → QR7');
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText('QR2 → QR7');
});

test("Operator sees why legacy stations are unavailable and can create after reload without changing roles", async ({ page }) => {
    const { stations, writes } = await setup(page, "Warehouse Operator");
    for (const station of stations.slice(0, 2)) Object.assign(station, { arucoId: null, usable: false, unavailableReason: "Navigation checkpoint is not configured." });
    await page.goto("/dashboard/tasks");
    await page.getByRole("button", { name: "Create Transport Task", exact: true }).click();
    const pickup = page.getByLabel("Pickup station", { exact: true });
    const delivery = page.getByLabel("Delivery station", { exact: true });
    await expect(pickup.locator('option[value="1"]')).toBeDisabled();
    await expect(delivery.locator('option[value="2"]')).toBeDisabled();
    await expect(pickup.locator('option[value="1"]')).toContainText("Navigation checkpoint is not configured");
    await expect(page.getByText("You do not need to change roles to create a task.", { exact: false })).toBeVisible();
    await expect(page.getByRole("button", { name: "Create Task (Manual)", exact: true })).toBeDisabled();
    await expect(page.getByRole("button", { name: "Cancel", exact: true })).toBeEnabled();
    Object.assign(stations[0], { arucoId: 2, usable: true, unavailableReason: null });
    Object.assign(stations[1], { arucoId: 7, usable: true, unavailableReason: null });
    await page.getByRole("button", { name: "Reload stations", exact: true }).click();
    await expect(pickup.locator('option[value="1"]')).toBeEnabled();
    await pickup.selectOption("1"); await delivery.selectOption("2");
    await expect(page.getByRole("button", { name: "Create Task (Manual)", exact: true })).toBeEnabled();
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText("QR2 → QR7");
    expect(writes).toHaveLength(0); // Reading/creating tasks never commissions stations with Operator credentials.
});

test("All map QR points appear at both ends, the reserved intersection is disabled, and reverse transfers work", async ({ page }) => {
    await setup(page, "Warehouse Operator");
    const stations = Array.from({ length: 9 }, (_, qr) => ({ stationId: qr + 10, zoneId: 1, warehouseId: 1,
        code: `POINT${qr}`, name: `Goods point ${qr}`, type: "TRANSFER", arucoId: qr, isActive: true, usable: qr !== 1 }));
    await page.route("https://localhost:7166/api/v1/warehouse-catalog", route => route.fulfill({ json: {
        warehouses: [{ warehouseId: 1, code: "SIM", name: "Simulation", isActive: true }], zones: [], stations,
        navigationWarehouseId: 1,
        transportPoints: stations.map(s => ({ warehouseId: 1, arucoId: s.arucoId, stationId: s.arucoId === 1 ? null : s.stationId,
            stationCode: s.code, pickupEligible: s.usable, deliveryEligible: s.usable,
            unavailableReason: s.usable ? null : "Reserved intersection: transit only, no goods transfer." })),
    } }));
    await page.goto("/dashboard/tasks"); await page.getByRole("button", { name: "Create Transport Task", exact: true }).click();
    const pickup = page.getByLabel("Pickup station", { exact: true }), delivery = page.getByLabel("Delivery station", { exact: true });
    await expect(pickup.locator("option")).toHaveCount(10); await expect(delivery.locator("option")).toHaveCount(10);
    await expect(pickup.locator('option[value="blocked-1"]')).toBeDisabled();
    await expect(delivery.locator('option[value="blocked-1"]')).toBeDisabled();
    for (const qr of [0, 2, 3, 4, 5, 6, 7, 8]) await expect(pickup.locator(`option[value="${qr + 10}"]`)).toBeEnabled();
    await pickup.selectOption("17"); await delivery.selectOption("12");
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText("QR7 → QR2");
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText("QR7 → QR2");
    await expect(delivery.locator('option[value="17"]')).toBeDisabled();
    await expect(page.getByRole("button", { name: "Create Task (Manual)", exact: true })).toBeEnabled();
});
