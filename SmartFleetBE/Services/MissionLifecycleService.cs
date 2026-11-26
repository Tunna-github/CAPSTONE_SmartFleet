using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Constants;
using SmartFleetBE.DTOs;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public enum MissionEventResult
{
    Applied,
    Duplicate,
    Rejected
}

public sealed class MissionLifecycleService(SmartFleetDbContext db,
    IConfiguration configuration, QrDemoRouteService qrDemo, TaskInterventionService interventions)
{
    public async Task<MissionEventResult> HandleAsync(
        string robotCode,
        RobotMissionEvent message,
        CancellationToken ct = default)
    {
        if (message.Event is "CONTROL_STOPPED" or "HOME_REACHED")
            return await interventions.HandleEvent(robotCode, message, ct);
        // =====================================================
        // 1. VALIDATE EVENT
        // =====================================================

        if (message.TaskId <= 0 ||
            message.AssignmentId <= 0 ||
            message.Event is not (
                "MISSION_STARTED"
                or "MISSION_COMPLETED"
                or "MISSION_FAILED"))
        {
            return MissionEventResult.Rejected;
        }

        await using var tx =
            await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct
            );

        // A late completion/start cannot bypass a persistent safety hold.
        if (await interventions.IsHeld(message.TaskId, ct))
            return MissionEventResult.Rejected;

        // =====================================================
        // 2. LOCK + GET ROBOT
        // =====================================================

        var robotQuery = db.Robots.Where(r => r.RobotCode == robotCode);

        var robot =
            await robotQuery.SingleOrDefaultAsync(ct);

        if (robot is null ||
            !string.Equals(
                robot.RobotCode,
                robotCode,
                StringComparison.Ordinal))
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        // =====================================================
        // 3. GET ASSIGNMENT
        // =====================================================

        var assignment =
            await db.TaskAssignments
                .SingleOrDefaultAsync(
                    a =>
                        a.AssignmentId == message.AssignmentId &&
                        a.TaskId == message.TaskId &&
                        a.RobotId == robot.RobotId,
                    ct
                );

        if (assignment is null)
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        // =====================================================
        // 4. GET MISSION
        // =====================================================

        var mission =
            await db.Missions
                .SingleOrDefaultAsync(
                    m =>
                        m.AssignmentId ==
                        assignment.AssignmentId,
                    ct
                );

        if (mission is null)
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        if (message.MissionId.HasValue &&
            message.MissionId.Value != mission.MissionId)
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        // =====================================================
        // 5. HANDLE TERMINAL / DUPLICATE EVENTS
        // =====================================================

        if (mission.MissionStatus is
            "COMPLETED" or
            "FAILED" or
            "CANCELLED" or
            "ABORTED")
        {
            var duplicate =
                (
                    message.Event == "MISSION_COMPLETED" &&
                    mission.MissionStatus == "COMPLETED"
                )
                ||
                (
                    message.Event == "MISSION_FAILED" &&
                    mission.MissionStatus == "FAILED"
                );

            // A delayed start ACK must never authorize the old robot to execute
            // a mission that has already failed and been recovered/requeued.

            await tx.RollbackAsync(ct);

            return duplicate
                ? MissionEventResult.Duplicate
                : MissionEventResult.Rejected;
        }

        // =====================================================
        // 6. ASSIGNMENT MUST STILL BE ACTIVE
        // =====================================================

        if (assignment.AssignmentStatus != "ACTIVE")
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        // Check live completion evidence AFTER terminal duplicate handling.
        // A restart must not reject an already committed completion just because
        // its transient demo route is gone and the robot is retrying its ACK.
        if (message.Event == "MISSION_COMPLETED")
        {
            if (robot.ConnectionStatus != "ONLINE" || robot.OperationalStatus != "BUSY" ||
                !RobotPresencePolicy.IsFresh(robot, DateTime.UtcNow, configuration)) return MissionEventResult.Rejected;
            var verifiedStages = await db.DeliveryVerifications.Where(v => v.MissionId == mission.MissionId && v.IsVerified)
                .Select(v => v.VerificationStage).ToListAsync(ct);
            if (mission.MissionStatus != "AT_DELIVERY" || !verifiedStages.Contains("PICKUP") || !verifiedStages.Contains("DELIVERY"))
                return MissionEventResult.Rejected;
            if (configuration.GetValue<bool>("DemoRoute:Enabled") &&
                !await db.MissionExecutions.AnyAsync(e => e.MissionId == mission.MissionId &&
                    e.NextWaypointIndex == db.MissionWaypoints.Count(w => w.MissionId == mission.MissionId), ct))
                return MissionEventResult.Rejected;
        }

        // =====================================================
        // 7. GET TASK
        // =====================================================

        var task =
            await db.TransportTasks
                .SingleAsync(
                    t =>
                        t.TaskId ==
                        assignment.TaskId,
                    ct
                );

        if (task.TaskStatus is not (
            TransportTaskStatuses.Assigned
            or TransportTaskStatuses.Executing))
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Rejected;
        }

        // =====================================================
        // 8. DUPLICATE MISSION_STARTED
        // =====================================================

        if (
            message.Event == "MISSION_STARTED" &&
            mission.MissionStatus is
                ("NAVIGATING_TO_PICKUP" or "AT_PICKUP" or "NAVIGATING_TO_DELIVERY" or "AT_DELIVERY") &&
            task.TaskStatus ==
                TransportTaskStatuses.Executing
        )
        {
            await tx.RollbackAsync(ct);
            return MissionEventResult.Duplicate;
        }

        // =====================================================
        // 9. MAP ROBOT EVENT TO DB STATUSES
        // =====================================================

        var now = DateTime.UtcNow;

        var missionStatus =
            message.Event switch
            {
                "MISSION_STARTED" =>
                    "NAVIGATING_TO_PICKUP",

                "MISSION_COMPLETED" =>
                    "COMPLETED",

                "MISSION_FAILED" =>
                    "FAILED",

                _ =>
                    throw new InvalidOperationException(
                        "Unsupported mission event."
                    )
            };

        var taskStatus =
            message.Event switch
            {
                "MISSION_STARTED" =>
                    TransportTaskStatuses.Executing,

                "MISSION_COMPLETED" =>
                    "COMPLETED",

                "MISSION_FAILED" =>
                    "FAILED",

                _ =>
                    throw new InvalidOperationException(
                        "Unsupported task event."
                    )
            };

        var reason =
            string.IsNullOrWhiteSpace(message.Reason)
                ? message.Event
                : message.Reason[
                    ..Math.Min(
                        500,
                        message.Reason.Length
                    )
                ];

        // =====================================================
        // 10. MISSION STATUS HISTORY
        // =====================================================

        if (mission.MissionStatus != missionStatus)
        {
            db.MissionStatusHistories.Add(
                new MissionStatusHistory
                {
                    MissionId =
                        mission.MissionId,

                    PreviousStatus =
                        mission.MissionStatus,

                    NewStatus =
                        missionStatus,

                    Source =
                        "ROBOT_AGENT",

                    Reason =
                        reason,

                    ChangedAt =
                        now
                }
            );
        }

        // =====================================================
        // 11. TASK STATUS HISTORY
        // =====================================================

        if (task.TaskStatus != taskStatus)
        {
            db.TaskStatusHistories.Add(
                new TaskStatusHistory
                {
                    TaskId =
                        task.TaskId,

                    PreviousStatus =
                        task.TaskStatus,

                    NewStatus =
                        taskStatus,

                    Reason =
                        reason,

                    ChangedAt =
                        now
                }
            );
        }

        // =====================================================
        // 12. UPDATE CURRENT STATUSES
        // =====================================================

        mission.MissionStatus =
            missionStatus;

        task.TaskStatus =
            taskStatus;

        mission.LastProgressAt =
            now;

        // =====================================================
        // 13. MISSION STARTED
        // =====================================================

        if (message.Event == "MISSION_STARTED")
        {
            mission.StartExecutionTime ??=
                now;

            task.StartedAt ??=
                now;
        }

        // =====================================================
        // 14. MISSION FINISHED
        // =====================================================

        else
        {
            mission.EndExecutionTime =
                now;

            if (mission.StartExecutionTime.HasValue)
            {
                mission.TotalDurationSeconds =
                    (int)Math.Clamp(
                        (
                            now -
                            mission.StartExecutionTime.Value
                        ).TotalSeconds,
                        0,
                        int.MaxValue
                    );
            }

            // ---------------------------------------------
            // Assignment status
            // ---------------------------------------------

            assignment.AssignmentStatus =
                message.Event == "MISSION_COMPLETED"
                    ? "COMPLETED"
                    : "ACTIVE"; // Fault recovery retains the physical load/resource lock.

            assignment.EndedAt = message.Event == "MISSION_COMPLETED" ? now : null;

            // ---------------------------------------------
            // Task completion / failure
            // ---------------------------------------------

            if (message.Event == "MISSION_COMPLETED")
            {
                task.CompletedAt =
                    now;
            }
            else
            {
                mission.FailureReason =
                    reason;

                task.FailureReason =
                    reason;
                await OperationalAlertService.RaiseAsync(db, robot, "MISSION_FAILED", "ERROR", reason, now,
                    task.TaskId, mission.MissionId, ct);
            }

            // =================================================
            // 15. CHECK OTHER ACTIVE ASSIGNMENTS
            // =================================================

            var anotherAssignment =
                await db.TaskAssignments
                    .AnyAsync(
                        a =>
                            a.RobotId ==
                            robot.RobotId
                            &&
                            a.AssignmentId !=
                            assignment.AssignmentId
                            &&
                            a.AssignmentStatus ==
                            "ACTIVE",
                        ct
                    );

            // =================================================
            // 16. CHECK MAINTENANCE
            // =================================================

            var maintenance =
                await db.RobotMaintenanceRecords
                    .AnyAsync(
                        m =>
                            m.RobotId ==
                            robot.RobotId
                            &&
                            (
                                m.MaintenanceStatus ==
                                "SCHEDULED"
                                ||
                                m.MaintenanceStatus ==
                                "IN_PROGRESS"
                            ),
                        ct
                    );

            // =================================================
            // 17. CALCULATE NEXT ROBOT STATUS
            // =================================================

            var nextRobotStatus =
                message.Event == "MISSION_FAILED"
                    ? "ERROR"
                    : maintenance
                        ? "MAINTENANCE"
                        : configuration.GetValue<bool>("Charging:Enabled") ? "BUSY" : "AVAILABLE";
            if (message.Event == "MISSION_COMPLETED" && !maintenance && !anotherAssignment && configuration.GetValue<bool>("Charging:Enabled"))
                await ChargingService.QueueAfterDelivery(db, robot, mission.MissionId, ct);

            // =================================================
            // 18. RELEASE ROBOT
            // =================================================

            if (
                !anotherAssignment &&
                robot.OperationalStatus == "BUSY"
            )
            {
                db.RobotStatusHistories.Add(
                    new RobotStatusHistory
                    {
                        RobotId =
                            robot.RobotId,

                        PreviousOperationalStatus =
                            robot.OperationalStatus,

                        NewOperationalStatus =
                            nextRobotStatus,

                        Reason =
                            reason,

                        ChangedAt =
                            now
                    }
                );

                robot.OperationalStatus =
                    nextRobotStatus;

                robot.UpdatedAt =
                    now;
            }
        }

        // =====================================================
        // 19. SAVE + COMMIT
        // =====================================================

        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);

        if (mission.MissionStatus is "COMPLETED" or "FAILED") qrDemo.ForgetMission(mission.MissionId);

        return MissionEventResult.Applied;
    }
}
