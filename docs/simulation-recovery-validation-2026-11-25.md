# Parked robots, simulator recovery and heading validation

Observed on the local Docker installation on 25 November 2026.

## Findings

R02 repeatedly reported camera-confirmed QR4 before the failure. Routing considered intersection queues but did not exclude that idle robot's checkpoint. Task 7 / mission 6 therefore received a route through QR4. Webots later stopped answering HTTP/stream requests while its process remained running; both robot heartbeats expired. The database records mission failure from the heartbeat timeout and subsequent Pause commands. This evidence does not establish a Cancel command as the cause of the Webots engine stall.

Task 7 had an accepted pickup verification. Its FAILED mission retains an ACTIVE assignment and an acknowledged Pause for load recovery. Restarting the simulator does not certify unloading or complete that assignment.

The extended motion regression also exposed a camera solver difference between Windows and the pinned OpenCV 4.11 container. The captured QR1 corners `[[433,111],[330,109],[332,6],[435,8]]` produced no acceptable IPPE candidate in the container. SQPnP recovered a level solution with approximately zero projection error, under the same orientation/error/ambiguity bounds. These pixels are now a numerical regression test, alongside rejection of a physically tilted robot.

## Changes

- Camera-confirmed checkpoint occupancy and claimed corridor endpoints are persisted in navigation state. Another robot's occupied points are excluded from task and return-home routing. Disconnects do not erase physical occupancy.
- Runtime navigation returns STOP before another robot's occupied/claimed destination. A later camera-confirmed checkpoint releases the previous corridor. No simulation ground truth is used for this decision.
- Viewer sessions require fresh simulator pose reports. WebSocket connection/initial-response deadlines and a browser loading deadline produce a visible error. Errors from a replaced iframe cannot close the current viewer.
- ArUco corners use subpixel refinement. IPPE and SQPnP candidates share the existing projection/gravity/ambiguity checks. Rejected checkpoint heading frames stop and retry for at most five seconds; they cannot advance a waypoint or release an intersection.
- Local Docker account passwords were changed on request and verified by logging into each role. Credentials remain in the installation's ignored `.env` and PostgreSQL.

## Validation

| Check | Result |
|---|---|
| Backend suite, including parked-QR planning, runtime STOP/resume, persistent claims and competing destinations | 118 passed |
| Python suite on Windows and Docker Python 3.10 / OpenCV 4.11 | 39 passed on each |
| Targeted browser regressions for simulation, map and assignment | 15 passed; six simulation checks repeated after iframe ownership change |
| TypeScript and production Docker builds | Passed |
| Actual stalled simulator refuses a new viewer session | HTTP 503 with a useful message; healthy restarted simulator returns a session |
| Real browser reconnect | Three consecutive 3D connections, including two Reconnect clicks |
| R02 parked at QR4, R01 pickup QR2 / delivery QR5 | Completed with two FE07 verifications; route `0 -> 1 -> 2 -> 1 -> 0 -> 5` |
| Subsequent two concurrent AUTO missions, including waiting behind R02 at QR1 | Both completed; measured motion and four FE07 verifications persisted |
| Deliberately paused idle test simulator | New viewer session returns 503 after poses expire; after unpause it returns 200 with the same run ID and increasing pose sequence |

Motion regressions run in a separate Compose project/database on port 8082. The user's database on port 8080 keeps Task 7 held. The simulator was restarted to restore the unresponsive stream, which resets simulated physical positions to the saved world. Maintenance must separately confirm the load recovery before force cancellation or requeue.

The occupancy guard covers known checkpoints/corridors and does not replace obstacle sensing. The internal Webots engine stall was not diagnosed from a native stack trace. The tests do not establish physical robot safety or long-duration deployment reliability.

Reproduction commands and operational notes: [simulation-deployment.md](simulation-deployment.md).
