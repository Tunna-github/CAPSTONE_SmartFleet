import { test, expect, type Page } from "@playwright/test";

async function prepare(page: Page, role: string) {
  const user = { userId: 43, username: "test", fullName: "Test User", roles: [role] };
  const calls: { url: string; body: any }[] = [];
  let recovered = false;
  await page.addInitScript(user => {
    localStorage.setItem("smartfleet_access_token", "test-only");
    localStorage.setItem("smartfleet_user", JSON.stringify(user));
  }, user);
  await page.route("https://localhost:7166/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/users/me")) return route.fulfill({ json: user });
    if (path === "/api/v1/task-interventions" && request.method() === "GET")
      return route.fulfill({ json: recovered ? [] : [{ taskId: 1, assignmentId: 1, robotId: 1, robotCode: "ERROR-R1",
        taskTrackingCode: "TASK-FAIL", taskStatus: "FAILED", missionStatus: "FAILED", operationalStatus: "ERROR",
        connectionStatus: "ONLINE", pickupStationId: 12, pickupStationName: "Original Pickup A", intervention: null }] });
    if (path.endsWith("/recover-requeue") && request.method() === "POST") {
      calls.push({ url: path, body: request.postDataJSON() });
      recovered = true;
      return route.fulfill({ json: { message: "Recovered at pickup. Task QUEUED; wait 30 seconds." } });
    }
    if (path === "/api/v1/transport-tasks") return route.fulfill({ json: [{ taskId: 1,
      taskTrackingCode: "TASK-FAIL", taskStatus: recovered ? "QUEUED" : "FAILED", priorityLevel: 2,
      pickupStationName: "Original Pickup A", deliveryStationName: "Delivery B", itemDescription: "Test load" }] });
    if (path.startsWith("/hubs/")) return route.fulfill({ status: 503, json: {} });
    return route.fulfill({ json: [] });
  });
  return calls;
}

test("Maintenance can recover only after all three physical confirmations", async ({ page }) => {
  const calls = await prepare(page, "Maintenance Technician");
  await page.goto("/profile");
  await page.getByRole("button", { name: "Robot safety / Pause", exact: true }).click();
  await expect(page.getByText("Original pickup: Original Pickup A", { exact: true })).toBeVisible();
  const recover = page.getByRole("button", { name: "Recover & Requeue (wait 30s)", exact: true });
  await expect(recover).toBeDisabled();
  await page.getByRole("textbox", { name: "Reason" }).fill("Goods returned to original pickup");
  await page.getByRole("checkbox", { name: "I physically confirmed" }).check();
  await page.getByRole("checkbox", { name: "I confirmed the load is removed and accounted for.", exact: true }).check();
  await expect(recover).toBeDisabled();
  await page.getByRole("checkbox", { name: "I confirmed the load is back at this task's original pickup station.", exact: true }).check();
  await recover.click();
  await expect.poll(() => calls.length).toBe(1);
  expect(calls[0].body).toMatchObject({ physicalSafetyConfirmed: true, loadRemovedConfirmed: true,
    loadReturnedToPickupConfirmed: true, expectedAssignmentId: 1 });
  await expect(page.getByRole("status")).toContainText("QUEUED; wait 30 seconds");
  await expect(page.getByText("No active robot assignments.", { exact: true })).toBeVisible();
});

test("Operator sees FAILED as FAILED and cannot use Maintenance recovery", async ({ page }) => {
  await prepare(page, "Warehouse Operator");
  await page.goto("/dashboard/tasks");
  const failedRow = page.getByRole("row").filter({ hasText: "Test load" });
  await expect(failedRow).toContainText("FAILED");
  await expect(failedRow).not.toContainText("PENDING");
  await page.getByRole("button", { name: "FAILED", exact: true }).click();
  await expect(failedRow).toBeVisible();
  await page.getByRole("button", { name: "Robot safety / Pause", exact: true }).click();
  await expect(page.getByRole("button", { name: "Recover & Requeue (wait 30s)", exact: true })).toHaveCount(0);
});
