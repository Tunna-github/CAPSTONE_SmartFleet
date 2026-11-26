import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, role = "Warehouse Operator") {
    const user = { userId: 1, fullName: "Warehouse Operator", roles: [role] };
    await page.addInitScript(user => {
        localStorage.setItem("smartfleet_access_token", "test-only");
        localStorage.setItem("smartfleet_user", JSON.stringify(user));
        localStorage.setItem("smartfleet-theme", "dark");
    }, user);
    const mission = { missionId: 101, taskId: 5, robotCode: "R01", missionStatus: "AT_PICKUP", assignmentStatus: "ACTIVE",
        pickupArUcoId: 2, deliveryArUcoId: 7, lastReachedArUcoId: 2, nextWaypointIndex: 2, mapRevision: 5,
        pickupVerified: false, deliveryVerified: false, packageRequired: true, pickupVerificationCode: "P1", deliveryVerificationCode: "D1", maxQrAttempts: 3,
        pickupQrAttempts: 0, deliveryQrAttempts: 0, held: false, stationPresenceFresh: true, attempts: [], verifications: [],
        waypoints: [0, 2, 7].map((arucoId, i) => ({ arucoId, row: 2, col: i + 2, stage: i === 1 ? "PICKUP" : "DELIVERY" })) };
    const state = { failRead: false, loseResponse: false, sessionRequests: 0, posts: [] as any[] };
    await page.route("https://localhost:7166/**", async route => {
        const req = route.request(), path = new URL(req.url()).pathname;
        if (path.endsWith("/users/me")) return route.fulfill({ json: user });
        if (path === "/api/v1/simulation") return route.fulfill({ json: { enabled: true, mapCompatible: true, streamReady: true } });
        if (path.endsWith("/simulation/session")) { state.sessionRequests++; return route.fulfill({ json: { viewerPath: "/simulation-viewer.html" } }); }
        if (path === "/api/v1/missions") return route.fulfill({ status: state.failRead ? 503 : 200, json: state.failRead ? { message: "Mission state unavailable" } : [mission] });
        if (path.endsWith("/verifications")) {
            const body = req.postDataJSON(); state.posts.push(body);
            if (state.loseResponse) return route.abort("failed");
            if (body.scannedQr === "WRONG") { mission.pickupQrAttempts++; return route.fulfill({ status: 422, json: { result: "QR_OR_PACKAGE_MISMATCH" } }); }
            if (body.stage === "PICKUP") { mission.pickupVerified = true; mission.missionStatus = "NAVIGATING_TO_DELIVERY"; }
            else { mission.deliveryVerified = true; mission.missionStatus = "COMPLETED"; mission.assignmentStatus = "COMPLETED"; }
            return route.fulfill({ json: { accepted: true } });
        }
        if (path === "/api/v1/warehouse-catalog") return route.fulfill({ json: { warehouses: [], zones: [], stations: [
            { stationId: 1, code: "P1", name: "Transfer point QR2", qrCodePayload: "QR2", type: "TRANSFER", arucoId: 2 },
            { stationId: 2, code: "D1", name: "Transfer point QR7", qrCodePayload: "QR7", type: "TRANSFER", arucoId: 7 } ] } });
        if (path.endsWith("/navigation/map")) return route.fulfill({ json: { revision: 5, rows: 18, columns: 28,
            cells: Array.from({ length: 18 }, (_, row) => Array.from({ length: 28 }, (_, col) => {
                const point = mission.waypoints.find(w => w.row === row && w.col === col);
                return point ? { type: "qr", arucoId: point.arucoId, label: `QR${point.arucoId}` } : { type: "floor" };
            })), links: [] } });
        if (path === "/api/v1/robots") return route.fulfill({ json: ["R01", "R02"].map((robotCode, i) => ({ robotId: i + 1, robotCode, batteryPercent: 85, operationalStatus: i ? "AVAILABLE" : "BUSY", connectionStatus: "ONLINE", openIncidents: [] })) });
        return route.fulfill({ json: path.endsWith("/positions") ? { R01: 2 } : path.endsWith("/simulation/poses") ? { mapRevision: 5, poses: [], serverTimeUtc: new Date().toISOString() } : [] });
    });
    await page.route("**/simulation-viewer.html?*", route => route.fulfill({ contentType: "text/html", body: '<body style="background:#142b3b;color:white">Test world<canvas width="600" height="400"></canvas><script>parent.postMessage({type:"smartfleet-simulation-ready"},location.origin)</script>' }));
    await page.goto("/simulation");
    return { mission, state };
}

test("3D iframe and unfinished handover survive view switches and internal navigation", async ({ page }) => {
    const { state } = await setup(page);
    await page.getByRole("button", { name: "Connect 3D", exact: true }).click();
    const iframe = page.locator('iframe[title="Live Webots 3D simulation"]');
    await expect(iframe).toBeVisible();
    await iframe.evaluate(el => (window as any).keptViewer = el);
    await page.getByLabel("Station QR payload").fill("P1");
    await page.getByRole("button", { name: "Fleet map 2D", exact: true }).click();
    await expect(iframe).toBeHidden();
    await expect(page.getByRole("form", { name: "PICKUP verification" })).toBeVisible();
    await page.getByRole("button", { name: "Transport tasks", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Transport Task Management" })).toBeVisible();
    await expect(iframe).toHaveCount(1);
    await page.getByRole("button", { name: "Live tracking & handover", exact: true }).click();
    await expect(page.getByLabel("Station QR payload")).toHaveValue("P1");
    await page.getByRole("button", { name: "Webots 3D", exact: true }).click();
    await expect(iframe).toBeVisible();
    expect(await iframe.evaluate(el => el === (window as any).keptViewer)).toBe(true);
    expect(state.sessionRequests).toBe(1);
    await expect(page.getByRole("link", { name: "Open tracking in new tab" })).toHaveAttribute("target", "_blank");
    await page.getByRole("button", { name: "Sign Out", exact: true }).click();
    await expect(iframe).toHaveCount(0);
});

test("Expired viewing session reconnects without losing an unfinished transfer or submitting it", async ({ page }) => {
    const { state } = await setup(page);
    await page.getByRole("button", { name: "Connect 3D", exact: true }).click();
    await expect(page.getByText("3D connected", { exact: true })).toBeVisible();
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await form.getByLabel("Station QR payload").fill("P1");
    await form.getByLabel("Package code").fill("BOX-1");
    await form.getByRole("checkbox").check();
    await page.frameLocator('iframe').locator('body').evaluate(() => {
        parent.postMessage({ type: "smartfleet-simulation-error", message: "Viewing session expired", recoverable: true }, location.origin);
    });
    await expect.poll(() => state.sessionRequests).toBe(2);
    await expect(page.getByText("3D connected", { exact: true })).toBeVisible();
    await expect(form.getByLabel("Station QR payload")).toHaveValue("P1");
    await expect(form.getByLabel("Package code")).toHaveValue("BOX-1");
    await expect(form.getByRole("checkbox")).toBeChecked();
    expect(state.posts).toHaveLength(0);
});

test("Repeated stream failures stop automatic reconnection after three retries", async ({ page }) => {
    const { state } = await setup(page);
    await page.route("**/simulation-viewer.html?*", route => route.fulfill({ contentType: "text/html", body:
        '<script>parent.postMessage({type:"smartfleet-simulation-error",message:"Connection lost",recoverable:true},location.origin)</script>' }));
    await page.getByRole("button", { name: "Connect 3D", exact: true }).click();
    await expect.poll(() => state.sessionRequests, { timeout: 15000 }).toBe(4);
    await expect(page.locator('iframe')).toHaveCount(0);
    await expect(page.getByRole("alert")).toContainText("Connection lost");
    await page.waitForTimeout(1500);
    expect(state.sessionRequests).toBe(4);
    expect(state.posts).toHaveLength(0);
});

test("Operator verifies pickup beside 2D and delivery beside 3D without bypassing station/package checks", async ({ page }, info) => {
    const { mission, state } = await setup(page);
    await page.getByRole("button", { name: "Fleet map 2D", exact: true }).click();
    expect(await page.getByLabel("Live robot status").evaluate(el => {
        const area = el.getBoundingClientRect();
        return [...el.querySelectorAll("button")].every(card => {
            const bounds = card.getBoundingClientRect();
            return bounds.top >= area.top && bounds.bottom <= area.bottom;
        });
    })).toBe(true);
    const panel = page.getByRole("complementary", { name: "Mission operations" });
    await expect(panel.getByText("QR2 → QR7", { exact: false })).toBeVisible();
    const form = page.getByRole("form", { name: "PICKUP verification" });
    const button = form.getByRole("button", { name: "Confirm pickup", exact: true });
    await expect(form.getByLabel("Station QR payload")).toBeEmpty();
    await expect(button).toBeDisabled();
    await form.getByLabel("Station QR payload").fill("WRONG");
    await form.getByLabel("Package code").fill("BOX-1");
    await expect(button).toBeDisabled(); await form.getByRole("checkbox").check(); await button.click();
    await expect(form.getByRole("alert")).toContainText("QR_OR_PACKAGE_MISMATCH");
    await form.getByLabel("Station QR payload").fill("P1");
    await page.screenshot({ path: info.outputPath("operations-2d-pickup.png"), fullPage: true });
    await button.click();
    await expect(form).toHaveCount(0); expect(state.posts[1].stage).toBe("PICKUP");
    mission.missionStatus = "AT_DELIVERY"; mission.nextWaypointIndex = 3;
    await page.getByRole("button", { name: "Webots 3D", exact: true }).click();
    await page.getByRole("button", { name: "Connect 3D", exact: true }).click();
    const delivery = page.getByRole("form", { name: "DELIVERY verification" });
    await delivery.getByLabel("Station QR payload").fill("D1"); await delivery.getByLabel("Package code").fill("BOX-1");
    await delivery.getByRole("checkbox").check();
    await page.screenshot({ path: info.outputPath("operations-3d-delivery.png"), fullPage: true });
    await delivery.getByRole("button", { name: "Confirm delivery", exact: true }).click();
    await expect(delivery).toHaveCount(0); expect(state.posts[2]).toMatchObject({ stage: "DELIVERY", scannedQr: "D1", physicalTransferConfirmed: true });
});

for (const role of ["Administrator", "Maintenance Technician"]) test(`${role} tracks transfers without Operator controls`, async ({ page }) => {
    const { state } = await setup(page, role);
    await expect(page.getByText("Waiting for Warehouse Operator to verify pickup.")).toBeVisible();
    await expect(page.getByRole("form", { name: "PICKUP verification" })).toHaveCount(0);
    expect(state.posts).toHaveLength(0);
});

test("Holds, stale presence and lost mission reads lock confirmation; unknown robot does not show another robot's transfer", async ({ page }) => {
    const { mission, state } = await setup(page);
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await form.getByLabel("Station QR payload").fill("P1"); await form.getByLabel("Package code").fill("BOX-1"); await form.getByRole("checkbox").check();
    const button = form.getByRole("button", { name: "Confirm pickup" });
    await expect(button).toBeEnabled(); mission.held = true;
    await expect(button).toBeDisabled(); mission.held = false; mission.stationPresenceFresh = false;
    await expect(page.getByText("Safety hold active.", { exact: false })).toHaveCount(0);
    await expect(button).toBeDisabled(); mission.stationPresenceFresh = true;
    await expect(button).toBeEnabled(); state.failRead = true;
    await expect(page.getByRole("alert")).toContainText("Mission state unavailable"); await expect(button).toBeDisabled();
    state.failRead = false; await expect(button).toBeEnabled();
    await page.getByLabel("Tracking robot", { exact: true }).selectOption("R02");
    await expect(form).toHaveCount(0); await expect(page.getByText("R02 has no active mission")).toBeVisible(); expect(state.posts).toHaveLength(0);
});

test("Lost response retries the same transfer ID and manual fallback requires a reason", async ({ page }) => {
    const { state } = await setup(page);
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await form.getByLabel("Verification method").selectOption("MANUAL"); await form.getByRole("checkbox").check();
    const button = form.getByRole("button", { name: "Confirm pickup" }); await expect(button).toBeDisabled();
    await form.getByLabel("Manual verification reason").fill("Scanner damaged; goods physically inspected.");
    state.loseResponse = true; await button.click(); await expect(form.getByRole("alert")).toBeVisible();
    await expect(button).toBeEnabled(); state.loseResponse = false; await button.click();
    await expect(form).toHaveCount(0); expect(state.posts).toHaveLength(2); expect(state.posts[1].requestId).toBe(state.posts[0].requestId);
});

test("Pickup at QR7 uses a neutral point name and shows D1 as its barcode, not as a delivery role", async ({ page }) => {
    const { mission, state } = await setup(page);
    mission.pickupArUcoId = 7; mission.deliveryArUcoId = 2; mission.pickupVerificationCode = "D1"; mission.deliveryVerificationCode = "P1";
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await expect(form.getByRole("heading", { name: "Confirm pickup · QR7", exact: true })).toBeVisible();
    await expect(form.getByText("Transfer point QR7", { exact: true })).toBeVisible();
    await expect(form.getByLabel("Station verification code")).toContainText("D1");
    await expect(form.getByLabel("Station verification code")).toContainText("retains its original code");
    await expect(form.getByLabel("Station QR payload")).toBeEmpty();
    await expect(form.getByRole("button", { name: "Confirm pickup", exact: true })).toBeDisabled();
    await form.getByLabel("Station QR payload").fill("D1"); await form.getByLabel("Package code").fill("BOX-1");
    await form.getByRole("checkbox").check(); await form.getByRole("button", { name: "Confirm pickup", exact: true }).click();
    await expect.poll(() => state.posts.length).toBe(1);
    expect(state.posts[0]).toMatchObject({ stage: "PICKUP", scannedQr: "D1", physicalTransferConfirmed: true });
});

test("New mission displays QR7 from its frozen snapshot and never fills the scan automatically", async ({ page }) => {
    const { mission, state } = await setup(page);
    mission.pickupArUcoId = 7; mission.deliveryArUcoId = 2;
    mission.pickupVerificationCode = "QR7"; mission.deliveryVerificationCode = "QR2";
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await expect(form.getByRole("heading", { name: "Confirm pickup · QR7", exact: true })).toBeVisible();
    await expect(form.getByLabel("Station verification code")).toContainText("QR7");
    await expect(form.getByLabel("Station verification code")).not.toContainText("D1");
    await expect(form.getByLabel("Station QR payload")).toBeEmpty();
    await form.getByLabel("Station QR payload").fill("QR7"); await form.getByLabel("Package code").fill("BOX-1");
    const button = form.getByRole("button", { name: "Confirm pickup", exact: true });
    await expect(button).toBeDisabled(); await form.getByRole("checkbox").check(); await button.click();
    await expect.poll(() => state.posts.length).toBe(1); expect(state.posts[0].scannedQr).toBe("QR7");
});

test("Missing frozen verification code never falls back to the mutable station catalog", async ({ page }) => {
    const { mission, state } = await setup(page);
    Object.assign(mission, { pickupVerificationCode: null });
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await expect(form.getByRole("status")).toContainText("saved verification code");
    await form.getByLabel("Station QR payload").fill("QR2"); await form.getByLabel("Package code").fill("BOX-1");
    await form.getByRole("checkbox").check();
    await expect(form.getByRole("button", { name: "Confirm pickup", exact: true })).toBeDisabled();
    expect(state.posts).toHaveLength(0);
});

test("Tracking fits narrow screens and remains readable in both themes", async ({ page }, info) => {
    await page.setViewportSize({ width: 390, height: 844 }); await setup(page);
    await page.getByRole("button", { name: "Fleet map 2D", exact: true }).click();
    await expect(page.getByRole("form", { name: "PICKUP verification" })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath("operations-mobile-dark.png"), fullPage: true });
    await page.getByRole("button", { name: "Switch to Light", exact: true }).click();
    await expect(page.locator("html")).toHaveClass(/light/);
    await page.waitForTimeout(300); // Allow the theme color transition to finish before visual QA.
    await page.screenshot({ path: info.outputPath("operations-mobile-light.png"), fullPage: true });
    await page.getByRole("form", { name: "PICKUP verification" }).scrollIntoViewIfNeeded();
    await page.screenshot({ path: info.outputPath("operations-mobile-handover.png"), fullPage: true });
});
