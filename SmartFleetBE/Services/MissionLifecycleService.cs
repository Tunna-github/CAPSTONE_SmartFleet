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

public sealed class MissionLifecycleService(
    SmartFleetDbContext db)
{
    public async Task<MissionEventResult> HandleAsync(
        string robotCode,
        RobotMissionEvent message,
        CancellationToken ct = default)
    {
        // =====================================================
        // 1. BASIC MESSAGE VALIDATION
        // =====================================================

        if (message.TaskId <= 0 ||
            message.AssignmentId <= 0 ||
            message.Event is not
                ("MISSION_STARTED"
                or "MISSION_COMPLETED"
                or "MISSION_FAILED"))
        {
            return MissionEventResult.Rejected;
        }


        await using var transaction =
            await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                ct);


        // =====================================================
        // 2. LOCK ROBOT
        // =====================================================

        var robotQuery =
            db.Database.IsSqlServer()
                ? db.Robots.FromSqlInterpolated(
                    $"SELECT * FROM dbo.Robots WITH (UPDLOCK, HOLDLOCK) WHERE RobotCode = {robotCode}")
                : db.Robots.Where(
                    r => r.RobotCode == robotCode);


        var robot =
            await robotQuery.SingleOrDefaultAsync(ct);


        if (robot is null ||
            !string.Equals(
                robot.RobotCode,
                robotCode,
                StringComparison.Ordinal))
        {
            return MissionEventResult.Rejected;
        }


        // =====================================================
        // 3. FIND ASSIGNMENT
        // =====================================================

        var assignment =
            await db.TaskAssignments
                .SingleOrDefaultAsync(
                    a =>
                        a.AssignmentId ==
                            message.AssignmentId
                        &&
                        a.TaskId ==
                            message.TaskId
                        &&
                        a.RobotId ==
                            robot.RobotId,
                    ct);


        if (assignment is null)
        {
            return MissionEventResult.Rejected;
        }


        // =====================================================
        // 4. FIND MISSION
        // =====================================================

        var mission =
            await db.Missions
                .SingleOrDefaultAsync(
                    m =>
                        m.AssignmentId ==
                        assignment.AssignmentId,
                    ct);


        if (mission is null)
        {
            return MissionEventResult.Rejected;
        }


        if (message.MissionId.HasValue &&
            message.MissionId.Value !=
                mission.MissionId)
        {
            return MissionEventResult.Rejected;
        }


        // =====================================================
        // 5. TERMINAL MISSION CHECK
        // =====================================================

        if (mission.MissionStatus is
            "COMPLETED" or "FAILED" or "ABORTED")
        {
            if (mission.MissionStatus == "COMPLETED" &&
                message.Event == "MISSION_COMPLETED")
            {
                return MissionEventResult.Duplicate;
            }


            if (mission.MissionStatus == "FAILED" &&
                message.Event == "MISSION_FAILED")
            {
                return MissionEventResult.Duplicate;
            }


            return MissionEventResult.Rejected;
        }


        // =====================================================
        // 6. ASSIGNMENT MUST STILL BE ACTIVE
        // =====================================================

        if (assignment.AssignmentStatus != "ACTIVE")
        {
            return MissionEventResult.Rejected;
        }


        // =====================================================
        // 7. LOAD TASK
        // =====================================================

        var task =
            await db.TransportTasks
                .SingleAsync(
                    t =>
                        t.TaskId ==
                        assignment.TaskId,
                    ct);


        if (task.TaskStatus is not
            (TransportTaskStatuses.Assigned
            or TransportTaskStatuses.Executing))
        {
            return MissionEventResult.Rejected;
        }


        // Duplicate MISSION_STARTED.
        if (message.Event == "MISSION_STARTED" &&
            task.TaskStatus ==
                TransportTaskStatuses.Executing)
        {
            return MissionEventResult.Duplicate;
        }


        var now =
            DateTime.UtcNow;


        var reason =
            string.IsNullOrWhiteSpace(message.Reason)
                ? message.Event
                : message.Reason.Trim();


        if (reason.Length > 500)
        {
            reason =
                reason[..500];
        }


        // =====================================================
        // 8. MISSION STARTED
        // =====================================================

        if (message.Event == "MISSION_STARTED")
        {
            // -------------------------------------------------
            // Mission:
            // INITIALIZING -> NAVIGATING_TO_PICKUP
            // -------------------------------------------------

            var previousMissionStatus =
                mission.MissionStatus;


            if (mission.MissionStatus !=
                "NAVIGATING_TO_PICKUP")
            {
                db.MissionStatusHistories.Add(
                    new MissionStatusHistory
                    {
                        MissionId =
                            mission.MissionId,

                        PreviousStatus =
                            previousMissionStatus,

                        NewStatus =
                            "NAVIGATING_TO_PICKUP",

                        Source =
                            "ROBOT_AGENT",

                        Reason =
                            reason,

                        ChangedAt =
                            now
                    });


                mission.MissionStatus =
                    "NAVIGATING_TO_PICKUP";
            }


            mission.StartExecutionTime ??=
                now;

            mission.LastProgressAt =
                now;


            // -------------------------------------------------
            // Task:
            // ASSIGNED -> EXECUTING
            // -------------------------------------------------

            var previousTaskStatus =
                task.TaskStatus;


            if (task.TaskStatus !=
                TransportTaskStatuses.Executing)
            {
                db.TaskStatusHistories.Add(
                    new TaskStatusHistory
                    {
                        TaskId =
                            task.TaskId,

                        PreviousStatus =
                            previousTaskStatus,

                        NewStatus =
                            TransportTaskStatuses.Executing,

                        Reason =
                            reason,

                        ChangedAt =
                            now
                    });


                task.TaskStatus =
                    TransportTaskStatuses.Executing;
            }


            task.StartedAt ??=
                now;


            // -------------------------------------------------
            // Robot:
            // BUSY / ASSIGNED -> EXECUTING
            // -------------------------------------------------

            if (robot.OperationalStatus is
                "BUSY" or "ASSIGNED")
            {
                var previousRobotStatus =
                    robot.OperationalStatus;


                robot.OperationalStatus =
                    "EXECUTING";

                robot.UpdatedAt =
                    now;


                db.RobotStatusHistories.Add(
                    new RobotStatusHistory
                    {
                        RobotId =
                            robot.RobotId,

                        PreviousOperationalStatus =
                            previousRobotStatus,

                        NewOperationalStatus =
                            "EXECUTING",

                        Reason =
                            reason,

                        ChangedAt =
                            now
                    });
            }


            await db.SaveChangesAsync(ct);

            await transaction.CommitAsync(ct);

            return MissionEventResult.Applied;
        }


        // =====================================================
        // 9. MISSION COMPLETED / FAILED
        // =====================================================

        var completed =
            message.Event ==
            "MISSION_COMPLETED";


        var finalMissionStatus =
            completed
                ? "COMPLETED"
                : "FAILED";


        var finalTaskStatus =
            completed
                ? TransportTaskStatuses.Completed
                : TransportTaskStatuses.Failed;


        // -----------------------------------------------------
        // Mission final state
        // -----------------------------------------------------

        var previousFinalMissionStatus =
            mission.MissionStatus;


        if (mission.MissionStatus !=
            finalMissionStatus)
        {
            db.MissionStatusHistories.Add(
                new MissionStatusHistory
                {
                    MissionId =
                        mission.MissionId,

                    PreviousStatus =
                        previousFinalMissionStatus,

                    NewStatus =
                        finalMissionStatus,

                    Source =
                        "ROBOT_AGENT",

                    Reason =
                        reason,

                    ChangedAt =
                        now
                });
        }


        mission.MissionStatus =
            finalMissionStatus;

        mission.LastProgressAt =
            now;

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
                    int.MaxValue);
        }


        // -----------------------------------------------------
        // Task final state
        // -----------------------------------------------------

        var previousFinalTaskStatus =
            task.TaskStatus;


        if (task.TaskStatus !=
            finalTaskStatus)
        {
            db.TaskStatusHistories.Add(
                new TaskStatusHistory
                {
                    TaskId =
                        task.TaskId,

                    PreviousStatus =
                        previousFinalTaskStatus,

                    NewStatus =
                        finalTaskStatus,

                    Reason =
                        reason,

                    ChangedAt =
                        now
                });
        }


        task.TaskStatus =
            finalTaskStatus;


        // -----------------------------------------------------
        // Assignment final state
        // -----------------------------------------------------

        assignment.AssignmentStatus =
            completed
                ? "COMPLETED"
                : "FAILED";

        assignment.EndedAt =
            now;


        // -----------------------------------------------------
        // COMPLETED
        // -----------------------------------------------------

        if (completed)
        {
            task.CompletedAt =
                now;

            task.FailureReason =
                null;

            mission.FailureReason =
                null;
        }

        // -----------------------------------------------------
        // FAILED
        // -----------------------------------------------------

        else
        {
            task.FailureReason =
                reason;

            mission.FailureReason =
                reason;
        }


        // =====================================================
        // 10. CHECK WHETHER ROBOT MAY BE RELEASED
        // =====================================================

        var anotherActiveAssignment =
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
                    ct);


        var activeMaintenance =
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
                    ct);


        // =====================================================
        // 11. RELEASE ROBOT
        // =====================================================

        if (!anotherActiveAssignment &&
            robot.OperationalStatus is
                "BUSY" or "ASSIGNED" or "EXECUTING")
        {
            var previousRobotStatus =
                robot.OperationalStatus;


            var nextRobotStatus =
                completed
                    ? (
                        activeMaintenance
                            ? "MAINTENANCE"
                            : "AVAILABLE"
                    )
                    : "ERROR";


            robot.OperationalStatus =
                nextRobotStatus;

            robot.UpdatedAt =
                now;


            db.RobotStatusHistories.Add(
                new RobotStatusHistory
                {
                    RobotId =
                        robot.RobotId,

                    PreviousOperationalStatus =
                        previousRobotStatus,

                    NewOperationalStatus =
                        nextRobotStatus,

                    Reason =
                        reason,

                    ChangedAt =
                        now
                });
        }


        // =====================================================
        // 12. SAVE
        // =====================================================

        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);


        return MissionEventResult.Applied;
    }
}