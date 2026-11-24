import { test, expect, type Page } from "@playwright/test";

async function setup(page: Page, role = "Warehouse Operator") {
    const user = { userId: 1, fullName: "Verification Tester", username: "test", email: "test@example.invalid", roles: [role] };
    await page.addInitScript(user => { localStorage.setItem("smartfleet_access_token", "test-only"); localStorage.setItem("smartfleet_user", JSON.stringify(user)); }, user);
    const mission = { missionId: 101, assignmentId: 102, taskId: 103, robotCode: "R01", mapRevision: 5,
        startArUcoId: 0, pickupArUcoId: 1, deliveryArUcoId: 2, pickupWaypointIndex: 1, nextWaypointIndex: 2,
        lastReachedArUcoId: 1, pickupReached: true, deliveryReached: false,
        missionStatus: "AT_PICKUP", assignmentStatus: "ACTIVE", pickupVerified: false, deliveryVerified: false,
        packageRequired: true, maxQrAttempts: 3, pickupQrAttempts: 0, deliveryQrAttempts: 0,
        held: false, stationPresenceFresh: true, attempts: [] as any[], verifications: [] as any[],
        waypoints: [0,1,2].map((arucoId,i) => ({ arucoId, row: 0, col: i, stage: i===1 ? "PICKUP" : i===2 ? "DELIVERY" : "TO_PICKUP" })) };
    const posts: any[] = [];
    await page.route("https://localhost:7166/**", async route => {
        const req = route.request(), path = new URL(req.url()).pathname;
        if (path.endsWith("/users/me")) return route.fulfill({ json: user });
        if (path === "/api/v1/missions") return route.fulfill({ json: [mission] });
        if (path.endsWith("/verifications") && req.method()==="POST") {
            const body = req.postDataJSON(); posts.push(body);
            if (body.type === "QRCODE" && body.scannedQr === "WRONG") {
                mission.pickupQrAttempts++; return route.fulfill({ status: 422, json: { accepted: false, result: "QR_OR_PACKAGE_MISMATCH" } });
            }
            mission.pickupVerified = true; mission.missionStatus = "NAVIGATING_TO_DELIVERY";
            return route.fulfill({ json: { accepted: true, result: "VERIFIED" } });
        }
        return route.fulfill({ json: path.endsWith("/positions") || path.endsWith("/localizations") ? {} : [] });
    });
    await page.goto(role === "Administrator" ? "/admin/qr-missions" : "/dashboard/qr-missions");
    return { mission, posts };
}

test("Operator must confirm goods and scan station/package before pickup can continue", async ({ page }) => {
    const { posts } = await setup(page);
    const form = page.getByRole("form", { name: "PICKUP verification" });
    const button = form.getByRole("button", { name: "Confirm pickup" });
    await expect(button).toBeDisabled();
    await form.getByLabel("Station QR payload").fill("STATION:P1");
    await form.getByLabel("Package code").fill("BOX-1");
    await expect(button).toBeDisabled();
    await form.getByRole("checkbox").check(); await button.click();
    await expect.poll(() => posts.length).toBe(1);
    expect(posts[0].physicalTransferConfirmed).toBe(true);
    expect(posts[0].requestId).toMatch(/^[0-9a-f-]{36}$/);
    await expect(page.getByText("NAVIGATING_TO_DELIVERY", { exact: true })).toBeVisible();
});

test("Wrong QR leaves robot waiting; manual confirmation requires a reason", async ({ page }) => {
    const { posts } = await setup(page);
    const form = page.getByRole("form", { name: "PICKUP verification" });
    await form.getByLabel("Station QR payload").fill("WRONG"); await form.getByLabel("Package code").fill("BOX-1");
    await form.getByRole("checkbox").check(); await form.getByRole("button", { name: "Confirm pickup" }).click();
    await expect(form.getByRole("alert")).toContainText("QR_OR_PACKAGE_MISMATCH");
    await expect(page.getByText("AT_PICKUP", { exact: true })).toBeVisible();
    await form.getByLabel("Verification method").selectOption("MANUAL");
    await expect(form.getByRole("button", { name: "Confirm pickup" })).toBeDisabled();
    await form.getByLabel("Manual verification reason").fill("Scanner damaged; goods checked physically.");
    await form.getByRole("button", { name: "Confirm pickup" }).click();
    await expect.poll(() => posts.length).toBe(2);
    expect(posts[1].type).toBe("MANUAL"); expect(posts[1].requestId).not.toBe(posts[0].requestId);
});

test("Admin can inspect persisted mission but cannot confirm the transfer", async ({ page }) => {
    await setup(page,"Administrator");
    await expect(page.getByText("Waiting for Warehouse Operator to verify pickup.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Confirm pickup" })).toHaveCount(0);
});
