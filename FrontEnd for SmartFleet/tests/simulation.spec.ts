import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, role: string, enabled=true) {
  const user={userId:42,fullName:"Simulation user",roles:[role]};
  await page.addInitScript(user=>{
    localStorage.setItem("smartfleet_access_token","test-only");
    localStorage.setItem("smartfleet_user",JSON.stringify(user));
  },user);
  await page.route("https://localhost:7166/**",async route=>{
    const path=new URL(route.request().url()).pathname;
    if(path.endsWith("/users/me")) return route.fulfill({json:user});
    if(path==="/api/v1/simulation") return route.fulfill({json:{enabled,mapCompatible:enabled,sceneId:"test"}});
    if(path.endsWith("/navigation/map")) return route.fulfill({json:{revision:5,rows:18,columns:28,
      cells:Array.from({length:18},()=>Array.from({length:28},()=>({type:"floor"}))),links:[]}});
    if(path==="/api/v1/robots") return route.fulfill({json:[{robotId:1,robotCode:"R01",model:"TurtleBot3",
      batteryPercent:85,connectionStatus:"ONLINE",operationalStatus:"BUSY",openIncidents:[]}]});
    if(path.endsWith("/simulation/poses")) return route.fulfill({json:{mapRevision:5,serverTimeUtc:new Date().toISOString(),poses:[
      {robotCode:"R01",runId:"test",sequence:1,mapRevision:5,source:"SIMULATION_GROUND_TRUTH",frame:"GRID_COL_ROW",x:4.5,y:3,yaw:90,capturedAtUtc:new Date().toISOString()},
      {robotCode:"R99",runId:"test",sequence:1,mapRevision:4,source:"SIMULATION_GROUND_TRUTH",frame:"GRID_COL_ROW",x:2,y:2,yaw:0,capturedAtUtc:new Date().toISOString()}]}});
    return route.fulfill({json:path.endsWith("/positions")?{}:[]});
  });
  await page.goto("/simulation");
}

for(const role of ["Administrator","Warehouse Operator","Maintenance Technician"]) {
  test(`${role} sees live robot status and battery on the 2D map`,async({page})=>{
    await setup(page,role);
    await page.getByRole("button",{name:"Fleet map 2D",exact:true}).click();
    await expect(page.locator('[data-robot-code="R01"]')).toBeVisible();
    await expect(page.getByLabel("Live robot status")).toContainText("Battery 85%");
    await expect(page.getByLabel("Live robot status")).toContainText("BUSY");
    await expect(page.locator('[data-robot-code="R99"]')).toHaveCount(0);
    await expect(page.locator('[data-robot-code="R01"] title')).toContainText("Live simulation pose");
    await expect(page.locator('[data-robot-code="R01"] title')).toContainText("Stale simulation pose",{timeout:7000});
  });
}

test("Unconfigured server explains 3D availability and disables connecting",async({page})=>{
  await setup(page,"Warehouse Operator",false);
  await expect(page.getByRole("button",{name:"Connect 3D",exact:true})).toBeDisabled();
  await expect(page.getByRole("status")).toContainText("not configured");
});

test("Physical odometry pose remains visible when Webots 3D is disabled and is labelled as an estimate",async({page})=>{
  await setup(page,"Warehouse Operator",false);
  await page.route("https://localhost:7166/api/v1/simulation/poses",route=>route.fulfill({json:{
    mapRevision:5,serverTimeUtc:new Date().toISOString(),poses:[{
      robotCode:"R01",runId:"physical-session",sequence:2,mapRevision:5,source:"ARUCO_IMU_ODOMETRY",
      frame:"GRID_COL_ROW",x:4.25,y:3,yaw:-90,capturedAtUtc:new Date().toISOString()
    }]}}));
  await page.reload();
  await page.getByRole("button",{name:"Fleet map 2D",exact:true}).click();
  await expect(page.locator('[data-robot-code="R01"] title')).toContainText("Live physical pose estimate");
  await expect(page.getByLabel("Live robot status")).toContainText("Battery 85%");
  await page.getByRole("button",{name:"Webots 3D",exact:true}).click();
  await expect(page.getByRole("button",{name:"Connect 3D",exact:true})).toBeDisabled();
});

test("Unresponsive simulator reports a useful error instead of opening a stuck viewer",async({page})=>{
  await setup(page,"Warehouse Operator");
  await page.route("https://localhost:7166/api/v1/simulation/session",route=>route.fulfill({status:503,json:{
    message:"Webots is not reporting fresh positions. Check simulator logs."}}));
  await page.getByRole("button",{name:"Connect 3D",exact:true}).click();
  await expect(page.getByRole("alert")).toContainText("not reporting fresh positions");
  await expect(page.locator('iframe')).toHaveCount(0);
});

test("Viewer failure closes the stalled iframe and permits a new connection",async({page})=>{
  await setup(page,"Warehouse Operator");
  await page.route("https://localhost:7166/api/v1/simulation/session",route=>route.fulfill({json:{viewerPath:"/simulation-viewer.html"}}));
  await page.route("**/simulation-viewer.html?*",route=>route.fulfill({contentType:"text/html",body:
    '<script>parent.postMessage({type:"smartfleet-simulation-error",message:"Webots connection timed out"},location.origin)</script>'}));
  await page.getByRole("button",{name:"Connect 3D",exact:true}).click();
  await expect(page.getByRole("alert")).toContainText("timed out");
  await expect(page.locator('iframe')).toHaveCount(0);
  await expect(page.getByRole("button",{name:"Connect 3D",exact:true})).toBeEnabled();
});
