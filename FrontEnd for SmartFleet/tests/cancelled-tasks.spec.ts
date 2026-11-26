import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, blocked = false) {
  const user = { userId: 42, fullName: "Operator", roles: ["Warehouse Operator"] };
  const calls: { method: string; path: string; data?: any }[] = [];
  let tasks = [{ taskId: 1, taskStatus: "CANCELLED", priorityLevel: 2, taskTrackingCode: "CANCEL-TEST",
    pickupStationName: "Condiments", deliveryStationName: "Instant Noodles", payloadWeightKg: 1, dispatchMode: "MANUAL" }];
  await page.addInitScript(user => {
    localStorage.setItem("smartfleet_access_token", "test-only"); localStorage.setItem("smartfleet_user", JSON.stringify(user));
  }, user);
  await page.route("https://localhost:7166/**", async route => {
    const req = route.request(), path = new URL(req.url()).pathname;
    if (path.endsWith("/users/me")) return route.fulfill({ json: user });
    if (path === "/api/v1/transport-tasks") return route.fulfill({ json: tasks });
    if (path.endsWith("/requeue") || path.endsWith("/cancelled")) {
      calls.push({ method: req.method(), path, data: req.method() === "POST" ? req.postDataJSON() : undefined });
      if (blocked) return route.fulfill({ status: 409, json: { message: "Cancellation is still in progress. Wait for safe stop." } });
      tasks = path.endsWith("/requeue") ? tasks.map(t => ({ ...t, taskStatus: "QUEUED" })) : [];
      return path.endsWith("/requeue") ? route.fulfill({ json: { message: "Task requeued after 30 seconds." } }) : route.fulfill({ status: 204 });
    }
    if (path.startsWith("/hubs/")) return route.fulfill({ status: 503, json: {} });
    return route.fulfill({ json: path.endsWith("/positions") ? {} : [] });
  });
  await page.goto("/dashboard/tasks"); return calls;
}

test("Cancelled task can requeue only after reason and inventory confirmation", async ({ page }) => {
  const calls = await setup(page);
  await page.getByRole("button", { name: "Requeue task T001", exact: true }).click();
  const confirm = page.getByRole("button", { name: "Confirm requeue", exact: true });
  await expect(confirm).toBeDisabled();
  await page.getByLabel("Requeue reason", { exact: true }).fill("Goods ready for retry");
  await expect(confirm).toBeDisabled();
  await page.getByRole("checkbox", { name: "I confirm the goods are available" }).check();
  await confirm.click();
  await expect(page.getByRole("button", { name: "Assign robot to task T001", exact: true })).toBeVisible();
  expect(calls).toEqual([{ method: "POST", path: "/api/v1/transport-tasks/1/requeue", data: { reason: "Goods ready for retry", goodsAtPickupConfirmed: true } }]);
  await expect(page.getByRole("button", { name: "Requeue task T001", exact: true })).toHaveCount(0);
});

test("Delete cancelled task explains retained history and removes its row", async ({ page }) => {
  const calls = await setup(page);
  await page.getByRole("button", { name: "Delete task T001", exact: true }).click();
  await expect(page.getByText("Remove from the operations list; mission and audit history stay available.", { exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Delete cancelled task", exact: true }).click();
  await expect(page.getByRole("button", { name: "Requeue task T001", exact: true })).toHaveCount(0);
  expect(calls).toEqual([{ method: "DELETE", path: "/api/v1/transport-tasks/1/cancelled", data: undefined }]);
});

test("Unfinished cancellation shows the backend rejection and keeps the task", async ({ page }) => {
  const calls = await setup(page, true);
  await page.getByRole("button", { name: "Delete task T001", exact: true }).click();
  await page.getByRole("button", { name: "Delete cancelled task", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Cancellation is still in progress");
  await page.keyboard.press("Escape");
  await expect(page.getByRole("button", { name: "Requeue task T001", exact: true })).toBeVisible();
  expect(calls).toHaveLength(1);
});
