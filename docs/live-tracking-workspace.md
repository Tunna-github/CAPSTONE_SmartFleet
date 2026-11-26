# Live tracking and handover workspace

Open **Live tracking & handover** in the sidebar (`/simulation`).

- Switch between **Fleet map 2D** and **Webots 3D**. Connect 3D once per viewer session.
- The authenticated application shell retains the 3D iframe when switching views or navigating to Tasks, Analytics, or another internal page. Returning to tracking reuses the same iframe and WebSocket rather than creating another viewer session.
- **Open tracking in new tab** opens a dedicated monitoring workspace. That tab establishes its own authorized viewer connection; other users observe the same server simulation.
- A lost/expired viewing connection now requests a fresh authorized session and reconnects automatically (up to three attempts per minute). The pinned R2025a stream adapter reports errors inline instead of opening its misleading reset dialog. Reconnection replaces only the viewing iframe; transfer inputs and server tasks stay intact. The 15-minute session limit, JWT expiry and account revocation checks remain enforced. Reload/closing the tab requires connecting again; unavailable servers or expired sign-in require user action.
- Robot cards show reported connection, operational status, battery and active task in both 2D and 3D. Click a card or choose a robot to inspect its handover. Simulation battery values remain simulated; 2D poses use measured simulation coordinates with existing freshness indicators.

## Operator workflow

1. Create and assign a transport task using the configured pickup/delivery stations.
2. Open tracking. The right panel lists active missions, prioritizing ones awaiting a goods transfer when no individual robot is selected.
3. When the robot reaches pickup or delivery, the existing verification form appears beside either map view. The panel stays associated with the selected robot/task/mission; selecting an idle robot does not display another robot's confirmation form.
4. Scan the station QR payload and package code when required, physically check the goods, check the transfer confirmation, then submit. Station verification payloads are distinct from navigation ArUco IDs. They are never automatically filled or confirmed by this screen.
5. Manual verification still requires a reason. QR retry limits, safety holds, stopped-robot presence checks and backend authorization remain in force. Admin and Maintenance can inspect transfers; only users with the Warehouse Operator role can confirm them.
6. Open route details for station names, navigation QR IDs and camera-confirmed waypoint progress. Full verification/mission history remains available via the panel footer.

Mission snapshots refresh every two seconds while tracking is displayed. Failed reads or a snapshot older than six seconds lock confirmation; the backend independently rechecks all transfer conditions. Reopening tracking refreshes mission state. Verification retries preserve the same request ID when a response was lost. No simulator controls or world geometry were added or changed by this UI work.

## Validation

From the frontend directory:

```powershell
npx tsc --noEmit
npm run test:web -- --workers=3
npm run build
```

`tests/operations-workspace.spec.ts` exercises iframe identity across view/route changes, logout cleanup, pickup/delivery forms beside both map modes, incorrect QR rejection, package/physical confirmation requirements, manual reason and request-ID retry, role restrictions, stale snapshots, safety holds, robot selection, and mobile/light/dark layouts. Transfer scenarios in this browser suite use mocked API responses; backend verification rules have their separate service tests.

From the repository root with the Docker stack running:

```powershell
node tools/check_simulation_browser.mjs http://localhost:8080 --robot-code=R02
```

This check uses the real API/Webots viewer, verifies two independent viewers, verifies that a navigation round trip creates only one viewer session and preserves the iframe while 3D frames continue arriving, captures 2D/3D/light-theme screenshots, and confirms that attempted viewer simulation-control messages are discarded. It does not create a transport task or reset the simulation/database.

`node tools/check_viewer_recovery.mjs http://localhost:8080` additionally injects
an abnormal close event (1006) into one real R2025a browser viewer. It checks that
no reset dialog appears, a fresh authorized session reconnects automatically,
the server run ID stays unchanged, simulation steps keep advancing and mission
progress is preserved. It does not submit handovers or restart any container.
The frontend regression tests also cover retained transfer inputs and the
three-retry limit. On 26 November these 18 targeted UI tests, TypeScript checking
and the actual viewer recovery check passed; backend and Webots container IDs
and start times remained unchanged while Task 9 continued to delivery.

Point names are neutral (`Transfer point QR2`, `Transfer point QR7`). Confirmation headings include the current QR ID and show the station label code separately. New station payloads use the QR point ID; P1/D1 are retained only inside pre-upgrade mission snapshots. Inputs are never automatically filled.

Verification codes now come from the mission API snapshot, not from the current catalog. New SIM assignments use QR2/QR7; older running/held missions keep their frozen P1/D1 codes and show a legacy notice. The scan field remains empty and physical confirmation is still required.
