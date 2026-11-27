// Explicit live acceptance: click Admin buttons, change both worlds, preserve business records.
import { chromium } from "../FrontEnd for SmartFleet/node_modules/playwright/index.mjs";
import fs from "node:fs";
import path from "node:path";
import assert from "node:assert/strict";
if (!process.argv.includes("--run-demo-test")) throw Error("Use --run-demo-test on the idle shared simulation host");
const root = path.resolve(import.meta.dirname, "..");
const env = Object.fromEntries(fs.readFileSync(path.join(root, ".env"), "utf8").trim().split(/\r?\n/).map(l => { const i = l.indexOf("="); return [l.slice(0, i), l.slice(i + 1)]; }));
const base = `http://127.0.0.1:${env.WEB_PORT || 8080}`;
const out = process.env.SMARTFLEET_EVIDENCE_DIR; if (!out) throw Error("Set a private SMARTFLEET_EVIDENCE_DIR"); fs.mkdirSync(out, { recursive: true });
const login = await fetch(base + "/api/v1/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ usernameOrEmail: "admin", password: env.DEMO_ADMIN_PASSWORD }) });
assert.equal(login.status, 200); const session = await login.json();
const browser = await chromium.launch({ channel: "msedge", headless: true, args: ["--enable-webgl", "--ignore-gpu-blocklist"] });
const results = [];
const quick = process.argv.includes("--quick");
const tasks = process.env.SMARTFLEET_QUICK_TASKS_FILE ? JSON.parse(fs.readFileSync(process.env.SMARTFLEET_QUICK_TASKS_FILE, "utf8")) : null;
const operatorLogin = await fetch(base + "/api/v1/auth/login", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ usernameOrEmail: "operator", password: env.DEMO_OPERATOR_PASSWORD }) });
assert.equal(operatorLogin.status, 200); const operator = await operatorLogin.json();
const targets = process.env.SMARTFLEET_MAP_TEST_TARGETS?.split(",") ?? ["map1", "map2"];
assert.ok(targets.every(t => ["map1", "map2"].includes(t)));
try {
 const context = await browser.newContext({ viewport: { width: 1440, height: 1040 } });
 await context.addInitScript(session => { localStorage.setItem("smartfleet_access_token", session.accessToken); localStorage.setItem("smartfleet_user", JSON.stringify(session.user)); }, session);
 const page = await context.newPage(); await page.goto(base + "/simulation");
 if (quick && targets[0] === "map2") {
  await page.getByRole("button", { name: "Fleet map 2D", exact: true }).click();
  await page.locator('[data-robot-code="R02"]').waitFor({ timeout: 30000 });
  await page.screenshot({ path: path.join(out, "verified-map1-2d.png"), fullPage: true });
 }
 for (const target of targets) {
  await page.getByRole("button", { name: target === "map1" ? "Map 1" : "Map 2", exact: true }).click({ timeout: 300000 });
  await page.getByRole("textbox", { name: "Reason", exact: true }).fill("[Validation] Admin web selection switches the matching world safely");
  await page.getByRole("dialog").getByRole("checkbox").check();
  await page.screenshot({ path: path.join(out, target + "-confirmation.png"), fullPage: true });
  const accepted = page.waitForResponse(r => r.url().endsWith("/map-profiles/select") && r.request().method() === "POST");
  await page.getByRole("button", { name: "Back up & switch map" }).click();
  const response = await accepted; assert.equal(response.status(), 202, await response.text());
  const request = response.request().postDataJSON();
  // Retrying a lost response cannot queue a second reset.
  const duplicate = await context.request.post(base + "/api/v1/map-profiles/select", { headers: { Authorization: "Bearer " + session.accessToken }, data: request });
  assert.equal(duplicate.status(), 202);
  console.log("Accepted", target, request.requestId);
  let final; const states = []; const deadline = Date.now() + 360000;
  while (Date.now() < deadline) {
   try {
    const r = await context.request.get(base + "/api/v1/map-profiles", { headers: { Authorization: "Bearer " + session.accessToken }, timeout: 10000 });
    if (r.ok()) {
     const status = await r.json(); const job = status.selection.operation;
     if (job?.id === request.requestId) {
      if (states.at(-1) !== job.state) { states.push(job.state); console.log(target, job.state, job.message); }
      assert.ok(!["FAILED", "FAILED_HOLD"].includes(job.state), job.message);
      if (job.state === "SUCCEEDED") { assert.equal(status.activeKey, target); assert.ok(job.backupReady); final = job; break; }
     }
    }
   } catch (error) { if (String(error).includes("AssertionError")) throw error; }
   await page.waitForTimeout(2000);
  }
  assert.ok(final, "World did not complete before deadline");
  if (tasks && target === "map1") {
   const eligible = await context.request.get(base + `/api/v1/transport-tasks/${tasks.map1}/available-robots?includeIneligible=true`, { headers: { Authorization: "Bearer " + operator.accessToken } });
   assert.equal(eligible.status(), 200); assert.ok((await eligible.json()).some(r => r.robotCode === "R02" && r.isEligible));
   const inactive = await context.request.post(base + `/api/v1/transport-tasks/${tasks.map2}/assign`, { headers: { Authorization: "Bearer " + operator.accessToken }, data: { robotId: 2 } });
   assert.equal(inactive.status(), 409); assert.match(await inactive.text(), /inactive/);
   console.log("Independent Warehouse 1 route is eligible; Current Warehouse task remains blocked.");
  }
  await page.getByRole("button", { name: target === "map1" ? "Map 1 · Active" : "Map 2 · Active", exact: true }).waitFor();
  await page.getByRole("button", { name: "Fleet map 2D", exact: true }).click();
  await page.locator('[data-robot-code="R02"]').waitFor({ timeout: 30000 });
  await page.screenshot({ path: path.join(out, target + "-2d.png"), fullPage: true });
  if (!quick) {
  await page.getByRole("button", { name: "Webots 3D", exact: true }).click();
  const connect = page.getByRole("button", { name: "Connect 3D", exact: true });
  if (await connect.isVisible()) await connect.click();
  await page.getByText("3D connected", { exact: true }).waitFor({ timeout: 180000 });
  const frame = await (await page.locator('iframe[title="Live Webots 3D simulation"]').elementHandle()).contentFrame();
  await frame.waitForFunction(() => document.body.innerText.trim() === "", undefined, { timeout: 180000 });
  await page.screenshot({ path: path.join(out, target + "-3d.png"), fullPage: true });
  }
  results.push({ target, id: request.requestId, states, passed: true, backupVerified: true, rendered2DAnd3D: !quick });
 }
 if (quick && tasks) {
  const auth = { Authorization: "Bearer " + operator.accessToken };
  assert.equal((await context.request.post(base + `/api/v1/task-interventions/${tasks.map1}/cancel`, { headers: auth, data: { reason: "Independent Warehouse 1 validation finished; queued test never dispatched" } })).status(), 200);
  assert.equal((await context.request.delete(base + `/api/v1/transport-tasks/${tasks.map1}/cancelled`, { headers: auth })).status(), 204);
  const pose = async () => (await (await context.request.get(base + "/api/v1/simulation/poses", { headers: auth })).json()).poses.find(p => p.robotCode === "R02");
  const before = await pose();
  const assigned = await context.request.post(base + `/api/v1/transport-tasks/${tasks.map2}/assign`, { headers: auth, data: { robotId: 2 } });
  assert.equal(assigned.status(), 200, await assigned.text());
  let moved = false;
  for (let i = 0; i < 40; i++) { await page.waitForTimeout(500); const current = await pose(); if (Math.hypot(current.x - before.x, current.y - before.y) > .08) { moved = true; break; } }
  assert.ok(moved, "Robot must actually start moving; do not wait for the full mission");
  results.push({ quickTaskId: tasks.map2, robot: "R02", assigned: true, actualMovementObserved: true, fullMissionAwaited: false });
  await page.screenshot({ path: path.join(out, "quick-dispatch-moving.png"), fullPage: true });
 }
 fs.writeFileSync(path.join(out, "report.json"), JSON.stringify(results, null, 2)); console.log(JSON.stringify(results));
} finally { await browser.close(); }
