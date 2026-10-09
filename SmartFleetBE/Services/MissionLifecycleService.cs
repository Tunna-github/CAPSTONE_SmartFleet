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

public sealed class MissionLifecycleService(SmartFleetDbContext db)
{
    public async Task<MissionEventResult> HandleAsync(
        string robotCode,
        RobotMissionEvent message,
        CancellationToken ct = default)
    {
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

        // =====================================================
        // 2. LOCK + GET ROBOT
        // =====================================================

        var robotQuery = db.Database.IsSqlServer()
            ? db.Robots.FromSqlInterpolated(
                $"SELECT * FROM dbo.Robots WITH (UPDLOCK, HOLDLOCK) WHERE RobotCode = {robotCode}"
            )
            : db.Robots.Where(
                r => r.RobotCode == robotCode
            );

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
                message.Event == "MISSION_STARTED"
                ||
                (
                    message.Event == "MISSION_COMPLETED" &&
                    mission.MissionStatus == "COMPLETED"
                )
                ||
                (
                    message.Event == "MISSION_FAILED" &&
                    mission.MissionStatus == "FAILED"
                );

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
            mission.MissionStatus == "NAVIGATING_TO_PICKUP" &&
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
                    : "FAILED";

            assignment.EndedAt =
                now;

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
                        : "AVAILABLE";

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

        return MissionEventResult.Applied;
    }
}