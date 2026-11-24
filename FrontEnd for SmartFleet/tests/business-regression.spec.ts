import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, role = "Warehouse Operator") {
  const user = { userId: 42, fullName: "Audit User", roles: [role] };
  const updates: any[] = [], creates: any[] = [];
  let rows = [1, 2, 3].map(priorityLevel => ({ taskId: priorityLevel, taskStatus: "QUEUED", priorityLevel,
    warehouseId: 1, pickupStationId: 1, deliveryStationId: 2, pickupStationName: "Pickup A", deliveryStationName: "Delivery B",
    payloadWeightKg: 2, packageCode: "SKU-KEEP", itemDescription: "Keep description", completedAt: null, scheduledTime: null }));
  await page.addInitScript(user => {
    localStorage.setItem("smartfleet_access_token", "test-only"); localStorage.setItem("smartfleet_user", JSON.stringify(user));
  }, user);
  await page.route("https://localhost:7166/**", async route => {
    const req = route.request(), url = new URL(req.url());
    if (url.pathname.endsWith("/users/me")) return route.fulfill({ json: user });
    if (url.pathname === "/api/v1/demo-routing/health") return route.fulfill({ json: { enabled: false } });
    if (url.pathname === "/api/v1/transport-tasks") {
      if (req.method() === "POST") { creates.push(req.postDataJSON()); return route.fulfill({ json: { ...rows[0], taskId: 10, ...req.postDataJSON() } }); }
      return route.fulfill({ json: rows });
    }
    const match = /\/transport-tasks\/(\d+)$/.exec(url.pathname);
    if (match) {
      const id = Number(match[1]);
      if (req.method() === "PUT") { updates.push(req.postDataJSON()); rows = rows.map(r => r.taskId === id ? { ...r, ...req.postDataJSON() } : r); }
      return route.fulfill({ json: rows.find(r => r.taskId === id) });
    }
    if (url.pathname === "/api/v1/navigation/map") return route.fulfill({ json: { revision: 5, rows: 18, columns: 28,
      cells: Array.from({ length: 18 }, () => Array.from({ length: 28 }, () => ({ type: "floor" }))), links: [] } });
    if (url.pathname === "/api/v1/robots") return route.fulfill({ json: [{ robotId: 99, robotCode: "REAL-R99", model: "JetRacer",
      connectionStatus: "ONLINE", operationalStatus: "AVAILABLE", batteryPercent: 80, openIncidents: [] }] });
    return route.fulfill({ json: url.pathname.endsWith("/positions") ? {} : [] });
  });
  return { updates, creates, setStatus: (status: string) => { rows[0].taskStatus = status; } };
}

test("backend priority 3 displays HIGH and sorts before priority 1", async ({ page }) => {
  await setup(page); await page.goto("/dashboard/tasks");
  const high = page.getByRole("row").filter({ hasText: "T003" });
  const low = page.getByRole("row").filter({ hasText: "T001" });
  await expect(high).toContainText("HIGH"); await expect(low).toContainText("LOW");
  const text = await page.getByRole("main").count() ? await page.getByRole("main").innerText() : await page.locator("body").innerText();
  expect(text.indexOf("T003")).toBeLessThan(text.indexOf("T001"));
});

test("Edit saves real IDs and preserves package, weight and schedule", async ({ page }) => {
  const { updates } = await setup(page); await page.goto("/dashboard/tasks");
  await page.getByRole("button", { name: "Edit task T001", exact: true }).click();
  await expect(page.getByLabel("Delivery Station ID", { exact: true })).toHaveValue("2");
  await page.getByLabel("Delivery Station ID", { exact: true }).fill("3");
  await page.getByRole("radio", { name: "HIGH", exact: true }).check();
  await page.getByRole("button", { name: "Confirm Update", exact: false }).click();
  await expect.poll(() => updates.length).toBe(1);
  expect(updates[0]).toEqual({ warehouseId: 1, pickupStationId: 1, deliveryStationId: 3, priorityLevel: 3,
    packageCode: "SKU-KEEP", itemDescription: "Keep description", payloadWeightKg: 2, scheduledTime: null });
  await expect(page.getByText("Task updated in the database.", { exact: true })).toBeVisible();
});

test("CRITICAL and unknown server states never appear as PENDING", async ({ page }) => {
  const data = await setup(page); data.setStatus("CRITICAL"); await page.goto("/dashboard/tasks");
  await expect(page.getByRole("row").filter({ hasText: "T001" })).toContainText("CRITICAL");
  data.setStatus("FUTURE_STATE"); await page.reload();
  await expect(page.getByRole("row").filter({ hasText: "T001" })).toContainText("UNKNOWN");
});

test("admin dashboard shows registered robot rather than seed assets", async ({ page }, testInfo) => {
  await setup(page, "Administrator"); await page.goto("/admin");
  await expect(page.getByText("REAL-R99", { exact: true })).toBeVisible();
  await expect(page.getByText("AMR-001", { exact: true })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Add Robot", exact: true })).toHaveCount(0);
  await expect(page.getByText("142", { exact: true })).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("admin-dashboard.png"), fullPage: true });
});

test("AUTO creation sends QR configuration in the same request with correct HIGH priority", async ({ page }) => {
  const { creates } = await setup(page);
  const separateQrPosts: string[] = [];
  await page.route("https://localhost:7166/api/v1/demo-routing/health", r => r.fulfill({ json: { enabled: true } }));
  await page.route("https://localhost:7166/api/v1/navigation/map", r => {
    const cells: any[][] = Array.from({ length: 18 }, () => Array.from({ length: 28 }, () => ({ type: "floor" })));
    cells[2][2] = { type: "qr", label: "QR0", arucoId: 0 }; cells[2][3] = { type: "qr", label: "QR1", arucoId: 1 };
    return r.fulfill({ json: { revision: 5, rows: 18, columns: 28, cells, links: [["QR0", "QR1"]] } });
  });
  page.on("request", request => { if (/\/tasks\/\d+\/qr$/.test(request.url()) && request.method() === "POST") separateQrPosts.push(request.url()); });
  await page.goto("/dashboard/tasks");
  await page.getByRole("button", { name: "Create Transport Task", exact: true }).click();
  await page.getByLabel("Warehouse ID *", { exact: true }).fill("1");
  await page.getByLabel("Pickup Station ID *", { exact: true }).fill("1");
  await page.getByLabel("Delivery Station ID *", { exact: true }).fill("2");
  await page.getByLabel("Weight(kg) *", { exact: true }).fill("1");
  await page.getByLabel(/^Pickup ArUco QR/).selectOption("0");
  await page.getByLabel(/^Delivery ArUco QR/).selectOption("1");
  await page.getByRole("radio", { name: /HIGH/ }).check();
  await page.getByRole("radio", { name: /^Automatic/ }).check();
  await page.getByRole("button", { name: "Create & Auto Assign", exact: true }).click();
  await expect.poll(() => creates.length).toBe(1);
  expect(creates[0]).toMatchObject({ dispatchMode: "AUTO", priorityLevel: 3, pickupArUcoId: 0, deliveryArUcoId: 1 });
  expect(separateQrPosts).toHaveLength(0);
});
