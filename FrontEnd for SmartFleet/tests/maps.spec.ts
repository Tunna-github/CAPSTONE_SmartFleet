import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, failSave = false) {
  const user = { userId: 42, fullName: "Map Admin", roles: ["Administrator"] };
  await page.addInitScript(user => {
    localStorage.setItem("smartfleet_access_token", "test-only");
    localStorage.setItem("smartfleet_user", JSON.stringify(user));
  }, user);
  let map = { revision: 1, rows: 18, columns: 28,
    cells: Array.from({ length: 18 }, () => Array.from({ length: 28 }, () => ({
      type: "floor", label: null, arucoId: null, isIntersection: false }))), links: [] };
  let writes = 0;
  await page.route("https://localhost:7166/**", async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path.endsWith("/users/me")) return route.fulfill({ json: user });
    if (path === "/api/v1/simulation") return route.fulfill({ json: { enabled: false } });
    if (path.endsWith("/navigation/map")) {
      if (request.method() === "PUT") {
        writes++;
        if (failSave) return route.fulfill({ status: 503, json: { message: "Map was not saved to the database. Please retry." } });
        const submitted = request.postDataJSON();
        expect(submitted.revision).toBe(map.revision);
        expect(submitted.cells).toHaveLength(18);
        expect(submitted.cells.every((row: unknown[]) => row.length === 28)).toBe(true);
        map = { ...submitted, revision: map.revision + 1 };
      }
      return route.fulfill({ json: map });
    }
    if (path.endsWith("/map/validation")) return route.fulfill({ json: {
      mapRevision: map.revision, qrNodeCount: 0, corridorCount: 0, readyForRouting: false,
      issues: ["Add at least two QR markers with physical ArUco IDs before assigning QR tasks."] } });
    return route.fulfill({ json: [] });
  });
  await page.goto("/admin/maps");
  await expect(page.getByText("Saved map revision: 1", { exact: false })).toBeVisible();
  return { writes: () => writes };
}

test("Map writes only on Save and reloads the returned database revision", async ({ page }) => {
  const state = await setup(page);
  expect(state.writes()).toBe(0);
  await page.getByRole("button", { name: "Save to Database", exact: true }).click();
  await expect(page.getByText("Saved map to database!", { exact: true })).toBeVisible();
  await expect(page.getByText("Saved map revision: 2", { exact: false })).toBeVisible();
  expect(state.writes()).toBe(1);
  await page.reload();
  await expect(page.getByText("Saved map revision: 2", { exact: false })).toBeVisible();
  expect(state.writes()).toBe(1);
});

test("Database failure shows an error without claiming success or incrementing revision", async ({ page }) => {
  await setup(page, true);
  await page.getByRole("button", { name: "Save to Database", exact: true }).click();
  await expect(page.getByText("Save failed: Map was not saved to the database. Please retry.", { exact: true })).toBeVisible();
  await expect(page.getByText("Saved map revision: 1", { exact: false })).toBeVisible();
  await expect(page.getByText("Saved map to database!", { exact: true })).toHaveCount(0);
});

test("Map without QR markers explains why it cannot dispatch QR tasks", async ({ page }) => {
  await setup(page);
  await expect(page.getByRole("status").filter({ hasText: "Not ready for QR tasks" }))
    .toContainText("Add at least two QR markers");
});
