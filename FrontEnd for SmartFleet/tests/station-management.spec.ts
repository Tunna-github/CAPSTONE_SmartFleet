import {test,expect,type Page} from "@playwright/test";

async function setup(page:Page,role="Administrator",reject=false) {
    const user={userId:1,fullName:"Station Admin",roles:[role]};const writes:{url:string;body:any}[]=[];
    const stations=[{stationId:1,zoneId:1,warehouseId:1,code:"P1",name:"Pickup One",type:"PICKUP",coordX:1,coordY:2,coordZ:0,yawAngle:0,qrCodePayload:"P1",arucoId:2,isActive:true,capacity:1,revision:3,usable:true,isOccupied:false,occupantRobotCodes:[],reservedRobotId:null},
        {stationId:2,zoneId:1,warehouseId:1,code:"D1",name:"Delivery One",type:"DELIVERY",coordX:2,coordY:3,coordZ:0,yawAngle:0,qrCodePayload:"D1",arucoId:7,isActive:true,capacity:1,revision:2,usable:true,isOccupied:false,occupantRobotCodes:[],reservedRobotId:null},
        {stationId:3,zoneId:1,warehouseId:1,code:"WAIT",name:"Waiting",type:"WAITING",coordX:0,coordY:0,coordZ:0,yawAngle:0,qrCodePayload:null,arucoId:8,isActive:true,capacity:1,revision:1,usable:true,isOccupied:true,occupantRobotCodes:["R02"],reservedRobotId:2}];
    const catalog={warehouses:[{warehouseId:1,code:"W1",name:"Warehouse One",isActive:true,revision:1}],zones:[{zoneId:1,warehouseId:1,code:"Z1",name:"Floor",type:"TRANSIT",speedLimit:.18,isActive:true,revision:1}],stations,
        robots:[{robotId:1,robotCode:"R01",warehouseId:1},{robotId:2,robotCode:"R02",warehouseId:1}],navigationWarehouseId:1,mapRevision:7,capacityPolicy:"One robot per marker; explicit reservation and physical clearance."};
    const cells:any[][]=Array.from({length:18},()=>Array.from({length:28},()=>({type:"floor"})));
    for(const [id,row,col] of [[2,1,4],[7,3,6],[8,1,6],[1,3,4]])cells[row][col]={type:"qr",label:`Marker-${id}`,arucoId:id,markerYawDeg:0,markerSizeM:.04,isIntersection:id===1};
    await page.addInitScript(user=>{localStorage.setItem("smartfleet_access_token","test-only");localStorage.setItem("smartfleet_user",JSON.stringify(user));},user);
    await page.route("https://localhost:7166/**",async route=>{
        const req=route.request(),url=new URL(req.url());
        if(url.pathname.endsWith('/users/me'))return route.fulfill({json:user});
        if(url.pathname==='/api/v1/warehouse-catalog')return route.fulfill({json:catalog});
        if(url.pathname.startsWith('/api/v1/warehouse-catalog/')){
            writes.push({url:url.pathname,body:req.postDataJSON()});
            if(reject)return route.fulfill({status:409,json:{message:"Warehouse has active assignments. Recover before catalog changes."}});
            return route.fulfill({json:{id:1}});
        }
        if(url.pathname==='/api/v1/navigation/map'){
            if(req.method()!=='GET')writes.push({url:url.pathname,body:req.postDataJSON()});
            return route.fulfill({json:{revision:7,rows:18,columns:28,cells,links:[]}});
        }
        if(url.pathname==='/api/v1/demo-routing/map/validation')return route.fulfill({json:{readyForRouting:true,qrNodeCount:4,corridorCount:3,issues:[]}});
        return route.fulfill({json:url.pathname.endsWith('/positions')?{}:[]});
    });
    return {writes};
}

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
    await expect(page.locator('svg text').filter({hasText:/^P1$/})).toHaveCount(1);
    await expect(page.locator('svg title').filter({hasText:'Pickup One'})).toHaveCount(1);
    await page.screenshot({path:info.outputPath('station-map-labels.png'),fullPage:true});
});

test("Task selects operational stations; WAITING and independent QR selectors are absent",async({page})=>{
    await setup(page,'Warehouse Operator');await page.goto('/dashboard/tasks');
    await page.getByRole('button',{name:'Create Transport Task',exact:true}).click();
    await page.getByLabel('Pickup station',{exact:true}).selectOption('1');await page.getByLabel('Delivery station',{exact:true}).selectOption('2');
    await expect(page.locator('select[name="pickupStationId"] option[value="3"]')).toHaveCount(0);
    await expect(page.locator('select[name="pickupArUcoId"]')).toHaveCount(0);
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText('P1 → QR2');
    await expect(page.getByText(/Navigation resolved by station:/)).toContainText('D1 → QR7');
});
