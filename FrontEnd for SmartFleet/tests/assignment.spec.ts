import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, mode: "list" | "empty" | "disappears" | "conflict") {
  let loads = 0;
  let disappeared = false;
  const assignments: any[] = [];
  const user = { userId: 42, fullName: "Test Operator", roles: ["Warehouse Operator"] };
  await page.addInitScript(user => {
    localStorage.setItem("smartfleet_access_token", "test-only");
    localStorage.setItem("smartfleet_user", JSON.stringify(user));
  }, user);
  await page.route("https://localhost:7166/**", async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.pathname.endsWith("/users/me")) return route.fulfill({ json: user });
    if (url.pathname === "/api/v1/transport-tasks") return route.fulfill({ json: [{ taskId: 1,
      taskStatus: "QUEUED", taskTrackingCode: "TASK-TEST", pickupStationName: "Pickup A",
      deliveryStationName: "Delivery B", itemDescription: "Assignment payload", priorityLevel: 2 }] });
    if (url.pathname.endsWith("/available-robots")) {
      loads++;
      expect(url.searchParams.get("includeIneligible")).toBe("true");
      if (mode === "empty") return route.fulfill({ json: [] });
      const reasons = [null, null, "Battery must be at least 20%", "Robot OFFLINE", "Robot status: MAINTENANCE", "Heartbeat/telemetry missing or stale"];
      const robots = reasons.map((reason, i) => ({ robotId: i + 1, robotCode: `R0${i + 1}`,
        batteryPercent: i === 2 ? 15 : 80, connectionStatus: "ONLINE", operationalStatus: "AVAILABLE",
        isEligible: reason === null, ineligibilityReason: reason }));
      if (mode === "disappears" && disappeared) robots[0] = { ...robots[0], isEligible: false, ineligibilityReason: "Robot OFFLINE" };
      return route.fulfill({ json: robots });
    }
    if (url.pathname.endsWith("/assign") && request.method() === "POST") {
      assignments.push(request.postDataJSON());
      return route.fulfill({ status: 409, json: { detail: "Robot is OFFLINE. Reload the robot list." } });
    }
    if (url.pathname.startsWith("/hubs/")) return route.fulfill({ status: 503, json: {} });
    return route.fulfill({ json: [] });
  });
  await page.goto("/dashboard/tasks");
  await page.getByRole("button", { name: "Assign robot to task T001", exact: true }).click();
  await expect(page.getByLabel("Available Robot", { exact: true })).toBeVisible();
  return { assignments, disappear: () => { disappeared = true; } };
}

test("Assign shows every robot with exclusion reasons and disables ineligible choices", async ({ page }) => {
  const { assignments: posts } = await setup(page, "list");
  const select = page.getByLabel("Available Robot", { exact: true });
  await expect(select.locator("option")).toHaveCount(7);
  await expect(page.getByText("2 eligible / 6 registered robots.", { exact: false })).toBeVisible();
  for (const id of [3, 4, 5, 6]) await expect(select.locator(`option[value='${id}']`)).toBeDisabled();
  await expect(page.getByRole("list", { name: "Unavailable robots" })).toContainText("Heartbeat/telemetry missing or stale");
  await expect(page.getByRole("button", { name: "Confirm Assignment", exact: true })).toBeDisabled();
  expect(posts).toHaveLength(0);
});

test("No available robot cannot submit an assignment", async ({ page }) => {
  const { assignments: posts } = await setup(page, "empty");
  await expect(page.getByText("No available robots for this task.", { exact: false })).toBeVisible();
  await expect(page.getByRole("button", { name: "Confirm Assignment", exact: true })).toBeDisabled();
  expect(posts).toHaveLength(0);
});

test("Robot disappearing between selection and confirmation is revalidated before POST", async ({ page }) => {
  const { assignments: posts, disappear } = await setup(page, "disappears");
  await page.getByLabel("Available Robot", { exact: true }).selectOption("1");
  disappear();
  await page.getByRole("button", { name: "Confirm Assignment", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("no longer eligible or connected");
  await expect(page.getByLabel("Available Robot", { exact: true })).toHaveValue("");
  expect(posts).toHaveLength(0);
});

test("Backend rejection after final selection remains an error and clears stale selection", async ({ page }) => {
  const { assignments: posts } = await setup(page, "conflict");
  await page.getByLabel("Available Robot", { exact: true }).selectOption("1");
  await page.getByRole("button", { name: "Confirm Assignment", exact: true }).click();
  await expect(page.getByRole("alert")).toContainText("Robot is OFFLINE");
  expect(posts).toEqual([{ robotId: 1 }]);
  await expect(page.getByRole("button", { name: "Confirm Assignment", exact: true })).toBeDisabled();
});
