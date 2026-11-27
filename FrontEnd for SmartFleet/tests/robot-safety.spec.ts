import { test, expect } from "@playwright/test";

for (const role of ["Warehouse Operator", "Maintenance Technician", "Administrator"]) {
  test(`${role}: sees Pause with appropriate cancellation permissions`, async ({ page }) => {
    const user = { userId: 42, fullName: "Test User", username: "test", email: "test@example.invalid", roles: [role] };
    const calls: { url: string; body: any }[] = [];
    await page.addInitScript(user => {
      localStorage.setItem("smartfleet_access_token", "test-only");
      localStorage.setItem("smartfleet_user", JSON.stringify(user));
    }, user);
    // All backend traffic is intercepted; no credentials, robot commands or real API calls.
    await page.route("https://localhost:7166/**", async route => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      if (path.endsWith("/users/me")) return route.fulfill({ json: user });
      if (path === "/api/v1/task-interventions" && request.method() === "GET")
        return route.fulfill({ json: [{ taskId: 1, assignmentId: 1, robotId: 1, robotCode: "TEST-R1", taskTrackingCode: "TASK-TEST",
          taskStatus: "EXECUTING", missionStatus: "NAVIGATING_TO_PICKUP", connectionStatus: "ONLINE",
          operationalStatus: "BUSY", intervention: { state: role === "Maintenance Technician" ? "CANCEL_REQUESTED" : "NORMAL", paused: true, commandAcknowledged: false } }] });
      if (path.startsWith("/api/v1/task-interventions/") && request.method() === "POST") {
        calls.push({ url: path, body: request.postDataJSON() });
        return route.fulfill({ json: { message: "Saved; waiting for robot acknowledgement" } });
      }
      if (path.startsWith("/hubs/")) return route.fulfill({ status: 503, json: {} });
      return route.fulfill({ json: [] });
    });
    await page.goto("/profile");
    await page.getByRole("button", { name: "Robot safety / Pause", exact: true }).click();
    await expect(page.getByRole("heading", { name: "TEST-R1", exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Pause / Stop", exact: true }).click();
    await expect.poll(() => calls.length).toBe(1);
    expect(calls[0].url).toMatch(/\/pause$/);
    expect(calls[0].body.reason).toBeTruthy();
    await expect(page.getByText("Waiting for robot acknowledgement", { exact: true })).toBeVisible();
    if (role === "Warehouse Operator") {
      await expect(page.getByRole("button", { name: "Cancel task & return" })).toBeDisabled();
      await expect(page.getByRole("button", { name: "Force cancel", exact: false })).toHaveCount(0);
      await page.getByRole("textbox", { name: "Reason" }).fill("Wrong SKU");
      await page.getByRole("button", { name: "Request Maintenance cancel" }).click();
      await expect.poll(() => calls.length).toBe(2);
      expect(calls[1].url).toMatch(/\/request-cancel$/);
    } else if (role === "Maintenance Technician") {
      const force = page.getByRole("button", { name: "Force cancel", exact: false });
      await expect(force).toBeDisabled();
      await page.getByRole("textbox", { name: "Reason" }).fill("Load removed and robot isolated");
      await page.getByRole("checkbox", { name: "I physically confirmed" }).check();
      await expect(force).toBeDisabled();
      await page.getByRole("checkbox", { name: "I confirmed the load is removed and accounted for.", exact: true }).check();
      await force.click();
      await expect.poll(() => calls.length).toBe(2);
      expect(calls[1].body.physicalSafetyConfirmed).toBe(true);
      expect(calls[1].body.loadRemovedConfirmed).toBe(true);
      await expect(page.getByRole("button", { name: "Cancel task & return" })).toHaveCount(0);
    } else {
      await expect(page.getByRole("button", { name: "Cancel task & return" })).toHaveCount(0);
      await expect(page.getByRole("button", { name: "Force cancel", exact: false })).toHaveCount(0);
    }
  });
}

for (const role of ["Warehouse Operator","Maintenance Technician","Administrator"]) test(`${role} resumes the same loaded mission after safety confirmation`,async({page})=>{
 const user={userId:1,fullName:"Resume tester",roles:[role]}, calls:any[]=[];
 let paused=true;
 await page.addInitScript(user=>{localStorage.setItem("smartfleet_access_token","test-only");localStorage.setItem("smartfleet_user",JSON.stringify(user));},user);
 await page.route("https://localhost:7166/**",async route=>{
  const req=route.request(),path=new URL(req.url()).pathname;
  if(path.endsWith("/users/me"))return route.fulfill({json:user});
  if(path==="/api/v1/task-interventions")return route.fulfill({json:[{taskId:1,assignmentId:7,robotId:1,robotCode:"RESUME-R1",taskTrackingCode:"TEST",taskStatus:"EXECUTING",missionStatus:"NAVIGATING_TO_DELIVERY",pickupVerified:true,connectionStatus:"ONLINE",operationalStatus:"BUSY",intervention:{state:"NORMAL",paused,commandAcknowledged:true,resumeSupported:true}}]});
  if(path.endsWith("/resume")){calls.push(req.postDataJSON());paused=false;return route.fulfill({json:{message:"Same mission resumed"}});}
  if(path.startsWith("/hubs/"))return route.fulfill({status:503,json:{}});
  return route.fulfill({json:[]});
 });
 await page.goto("/profile");await page.getByRole("button",{name:"Robot safety / Pause",exact:true}).click();
 const button=page.getByRole("button",{name:"Resume task",exact:true});await expect(button).toBeDisabled();
 await page.getByRole("textbox",{name:"Reason"}).fill("Path and loaded robot inspected");await expect(button).toBeDisabled();
 await page.getByRole("checkbox",{name:"I checked that the path, robot and current load are safe to continue.",exact:true}).check();await button.click();
 await expect.poll(()=>calls.length).toBe(1);
 expect(calls[0]).toMatchObject({expectedAssignmentId:7,physicalSafetyConfirmed:true,loadRemovedConfirmed:false,loadReturnedToPickupConfirmed:false});
 await expect(page.getByText("Same mission resumed",{exact:true})).toBeVisible();
 await expect(page.getByRole("button",{name:"Resume task",exact:true})).toHaveCount(0);
});
