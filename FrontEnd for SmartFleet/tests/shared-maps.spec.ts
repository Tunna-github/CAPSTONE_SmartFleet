import { test, expect, type Page } from "@playwright/test";
async function setup(page: Page, role = "Administrator") {
  const user = { userId: 42, fullName: role, roles: [role] };
  const state = { shared: true, key: "map2", blocked: null as string | null, offline: false, reject: false,
    job: null as null | { id: string; targetKey: string; state: string; message: string; backupReady: boolean }, requests: [] as Record<string, unknown>[] };
  await page.clock.install();
  await page.addInitScript(user => { localStorage.setItem("smartfleet_access_token", "test-only"); localStorage.setItem("smartfleet_user", JSON.stringify(user)); }, user);
  await page.route("https://localhost:7166/**", async route => {
    const req = route.request(), path = new URL(req.url()).pathname;
    if (path.endsWith("/users/me")) return route.fulfill({ json: user });
    if (path.endsWith("/map-profiles/select")) {
      const body = req.postDataJSON(); state.requests.push(body);
      if (state.reject) return route.fulfill({ status: 409, json: { message: "A robot has an active mission. Finish it first." } });
      state.job = { id: body.requestId, targetKey: body.targetKey, state: "REQUESTED", message: "Waiting for host", backupReady: false };
      return route.fulfill({ status: 202, json: { requestId: body.requestId } });
    }
    if (path.endsWith("/map-profiles")) {
      if (state.offline) return route.fulfill({ status: 503, json: {} });
      return route.fulfill({ json: { shared: state.shared, activeKey: state.key, profiles: [
        { key: "map1", name: "Map 1 - Original Aiven map", isActive: state.key === "map1" },
        { key: "map2", name: "Map 2 - Separated lanes and charging", isActive: state.key === "map2" }],
        selection: { available: true, blockedReason: state.blocked, operation: state.job } } });
    }
    if (path.endsWith("/simulation")) return route.fulfill({ json: { enabled: false, mapCompatible: true, sceneId: state.key } });
    if (path.startsWith("/hubs/")) return route.fulfill({ status: 503, json: {} });
    return route.fulfill({ json: path.endsWith("/positions") ? {} : [] });
  });
  return state;
}
async function confirm(page: Page) {
  await page.getByRole("button", { name: "Map 1", exact: true }).click();
  await expect(page.getByRole("button", { name: "Back up & switch map" })).toBeDisabled();
  await page.getByRole("textbox", { name: "Reason", exact: true }).fill("Verify classic grid");
  await page.getByRole("checkbox").check();
  await page.getByRole("button", { name: "Back up & switch map" }).click();
}
for (const role of ["Warehouse Operator", "Maintenance Technician"]) test(`${role} cannot switch maps`, async ({ page }) => {
  const state = await setup(page, role); await page.goto("/simulation");
  await expect(page.getByRole("button", { name: "Map 1", exact: true })).toBeDisabled(); expect(state.requests).toEqual([]);
});
test("Admin sees backup progress across server restart and the new active map", async ({ page }) => {
  const state = await setup(page); await page.goto("/simulation"); await confirm(page);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(state.requests).toHaveLength(1); expect(state.requests[0]).toMatchObject({ targetKey: "map1", expectedActiveKey: "map2", confirmed: true });
  state.job = { ...state.job!, state: "STOPPING_WORLD", message: "Stopping previous simulation world", backupReady: true };
  await page.clock.fastForward(3100); await expect(page.getByText("Database backup verified and retained on the simulation host.")).toBeVisible();
  state.offline = true; await page.clock.fastForward(3100); await expect(page.getByText(/server is restarting/)).toBeVisible();
  state.offline = false; state.key = "map1"; state.job = { ...state.job!, state: "SUCCEEDED", message: "New world verified" };
  await page.clock.fastForward(3100); await expect(page.getByRole("button", { name: "Map 1 · Active", exact: true })).toBeDisabled();
  await expect(page.getByText("New world verified")).toBeVisible(); expect(state.requests).toHaveLength(1);
});
for (const blocker of ["A robot has an active mission.", "A robot is returning to charge."]) test(`Disabled for: ${blocker}`, async ({ page }) => {
  const state = await setup(page); state.blocked = blocker; await page.goto("/simulation");
  await expect(page.getByRole("button", { name: "Map 1", exact: true })).toBeDisabled(); await expect(page.getByText(blocker, { exact: true })).toBeVisible(); expect(state.requests).toHaveLength(0);
});
test("Server rejects a safety race without changing maps", async ({ page }) => {
  const state = await setup(page); state.reject = true; await page.goto("/simulation"); await confirm(page);
  await expect(page.getByRole("alert")).toContainText("active mission"); expect(state.key).toBe("map2");
  await page.getByRole("button", { name: "Keep current map" }).click(); await expect(page.getByRole("dialog")).toHaveCount(0);
});
test("Local mode hides shared map selection", async ({ page }) => {
  const state = await setup(page); state.shared = false; await page.goto("/simulation");
  await expect(page.getByRole("button", { name: "Map 1", exact: true })).toHaveCount(0);
});
