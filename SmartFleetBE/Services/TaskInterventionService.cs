using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.DTOs;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public sealed record InterventionRequest(string Reason, bool PhysicalSafetyConfirmed = false,
    bool LoadRemovedConfirmed = false, bool LoadReturnedToPickupConfirmed = false,
    long? ExpectedAssignmentId = null);

public static class CancellationPolicy
{
    // A business reason never overrides evidence of a load or unsafe device.
    public static bool CanDirectCancel(string missionStatus, bool pickupVerified,
        bool healthy, bool freshTelemetry) => healthy && freshTelemetry && !pickupVerified &&
        missionStatus is "INITIALIZING" or "NAVIGATING_TO_PICKUP";
}

public sealed class TaskInterventionService(SmartFleetDbContext db,
    QrDemoRouteService routes, IConfiguration configuration, TimeProvider? clock = null,
    SmartFleetBE.Navigation.NavigationCoordinator? navigation = null)
{
    public async Task<bool> IsHeld(long taskId, CancellationToken ct) =>
        await db.TaskInterventions.AnyAsync(x => x.TaskId == taskId &&
            (x.Paused || x.State != "NORMAL"), ct);

    // The health monitor uses its existing transaction so state + durable stop
    // either commit together or both roll back. Existing interventions are preserved.
    public async Task QueueSystemPauseAsync(TaskAssignment assignment, string reason, DateTime now, CancellationToken ct)
    {
        var control = await db.TaskInterventions.SingleOrDefaultAsync(c => c.TaskId == assignment.TaskId, ct);
        if (control is { Paused: true } || control != null && control.State != "NORMAL") return;
        if (control == null) { control = new TaskIntervention { TaskId = assignment.TaskId }; db.TaskInterventions.Add(control); }
        Audit("SYSTEM_PAUSE", assignment.TaskId, null, reason, assignment, control, now);
        control.Paused = true; control.UpdatedAt = now;
        Queue(control, assignment, "PAUSE_TASK", now);
    }

    public async Task<object> Act(long taskId, string action, InterventionRequest request,
        int? userId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
            try { return await ActCore(taskId, action, request, userId, ct); }
            catch (Exception ex) when (action == "recover-requeue" && attempt < 4 && MissionExecutionService.IsConflict(ex))
            {
                // Recovery can race a final telemetry/stop acknowledgement.
                // Retry only transactions PostgreSQL explicitly rolled back.
                db.ChangeTracker.Clear();
                await Task.Delay(50 * attempt, ct);
            }
    }

    private async Task<object> ActCore(long taskId, string action, InterventionRequest request,
        int? userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 400)
            throw new ArgumentException("A reason of 1..400 characters is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (action=="resume" && !await MapSwitchGate.AllowsDispatch(db,configuration,ct))
            throw new InvalidOperationException("Map change/recovery hold blocks Resume.");
        var task = await db.TransportTasks.SingleOrDefaultAsync(t => t.TaskId == taskId && t.DeletedAt == null, ct)
            ?? throw new KeyNotFoundException("Task not found.");
        var assignment = await db.TaskAssignments.Include(a => a.Robot).Include(a => a.Mission)
            .SingleOrDefaultAsync(a => a.TaskId == taskId && a.AssignmentStatus == "ACTIVE", ct);
        if(assignment==null && action=="return-to-charge" && task.TaskStatus=="QUEUED")
            assignment=await db.TaskAssignments.Include(a=>a.Robot).Include(a=>a.Mission)
                .SingleOrDefaultAsync(a=>a.TaskId==taskId&&a.AssignmentId==request.ExpectedAssignmentId&&a.AssignmentStatus=="FAILED"&&
                    a.Robot.OperationalStatus=="MAINTENANCE"&&!db.TaskAssignments.Any(active=>active.RobotId==a.RobotId&&active.AssignmentStatus=="ACTIVE"),ct);
        var control = await db.TaskInterventions.SingleOrDefaultAsync(x => x.TaskId == taskId, ct);
        if (control == null)
        {
            control = new TaskIntervention { TaskId = taskId };
            db.TaskInterventions.Add(control);
        }
        if (action == "cancel" && task.TaskStatus == "CANCELLED")
            return new { message = "Cancellation already recorded; safety hold remains until confirmed." };
        if (task.TaskStatus == "COMPLETED" || task.TaskStatus == "CANCELLED" &&
            !(assignment != null && action is "pause" or "request-cancel" or "force-cancel" or "return-to-charge" or "recover-requeue"))
            throw new InvalidOperationException("Task is terminal.");
        if (action is "cancel" or "request-cancel" && control.State != "NORMAL" &&
            !(action == "request-cancel" && control.State is "CANCELLED_HOLD" or "RETURNING_HOME" or "CANCEL_STOP_PENDING"))
            throw new InvalidOperationException("An intervention is already pending. Use Maintenance recovery.");
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var robot = assignment?.Robot;
        var mission = assignment?.Mission;
        string? command = null;
        // Capture the device snapshot BEFORE force cancellation changes any status.
        Audit(action, taskId, userId, request.Reason.Trim(), assignment, control, now, request);
        switch (action)
        {
            case "resume":
                if (assignment?.Mission==null || request.ExpectedAssignmentId!=assignment.AssignmentId ||
                    control.State!="NORMAL" || !control.Paused || !control.CommandAcknowledged ||
                    !request.PhysicalSafetyConfirmed || control.PauseContextJson==null)
                    throw new InvalidOperationException("Resume requires the current paused assignment, robot stop acknowledgement and a safety confirmation. Cancellation/fault recovery cannot be resumed.");
                using (var context=JsonDocument.Parse(control.PauseContextJson)) {
                    await ValidateResume(assignment,context.RootElement,now,ct);
                    Queue(control,assignment,"RESUME_TASK",now,context.RootElement.Clone());
                }
                control.ResumeFailureReason=null;
                // Keep the overlay held until the same controller confirms its
                // retained mission. No task/load/assignment/berth/traffic changes.
                break;
            case "return-to-charge":
                var recovered=task.TaskStatus=="QUEUED" && assignment?.AssignmentStatus=="FAILED" && control.State=="NORMAL";
                if(!configuration.GetValue<bool>("Charging:Enabled") || !(task.TaskStatus=="CANCELLED"||recovered) ||
                    assignment?.Mission==null || request.ExpectedAssignmentId!=assignment.AssignmentId ||
                    !(control.State=="CANCELLED_HOLD"||recovered) || !control.CommandAcknowledged ||
                    !request.PhysicalSafetyConfirmed || !request.LoadRemovedConfirmed ||
                    !await EmptyAndHealthy(assignment,ct,recovered))
                    throw new InvalidOperationException("Maintenance must confirm a stopped, empty, healthy cancelled robot with its current assignment.");
                if(!routes.GetRobotPositions().TryGetValue(robot!.RobotCode,out var anchor))
                    throw new InvalidOperationException("No fresh camera QR. Inspect/relocalize the stopped robot at a checkpoint before requesting return; no blind movement is allowed.");
                PrepareReturn(control,assignment,anchor,now);
                break;
            case "recover-requeue":
                if (assignment == null || request.ExpectedAssignmentId != assignment.AssignmentId)
                    throw new InvalidOperationException("Assignment changed or was not specified. Refresh and physically confirm recovery for the current assignment.");
                if (assignment?.Mission == null ||
                    !(task.TaskStatus == "FAILED" || assignment.Mission.MissionStatus == "FAILED" ||
                      robot!.OperationalStatus == "ERROR" || control.State == "CANCEL_REQUESTED"))
                    throw new InvalidOperationException("Recovery requires a failed/interrupted task with an active assignment.");
                if (!request.PhysicalSafetyConfirmed || !request.LoadRemovedConfirmed ||
                    !request.LoadReturnedToPickupConfirmed)
                    throw new InvalidOperationException("Maintenance must confirm physical stop, load removal, and return of the load to this task's original pickup station.");
                if (!await db.Stations.AnyAsync(s => s.StationId == task.PickupStationId &&
                    s.Zone.WarehouseId == task.WarehouseId && s.Zone.IsActive && s.Zone.Warehouse.IsActive &&
                    (s.StationType == "PICKUP" || s.StationType == "TRANSFER") && s.IsActive, ct))
                    throw new InvalidOperationException("Original pickup station is missing or inactive.");
                assignment.AssignmentStatus = "FAILED";
                assignment.EndedAt = now;
                assignment.ReassignmentReason = request.Reason.Trim();
                if (assignment.Mission.MissionStatus != "FAILED")
                {
                    db.MissionStatusHistories.Add(new MissionStatusHistory {
                        MissionId = assignment.Mission.MissionId,
                        PreviousStatus = assignment.Mission.MissionStatus, NewStatus = "FAILED",
                        Source = "SYSTEM", ChangedBy = userId, Reason = request.Reason.Trim(), ChangedAt = now });
                    assignment.Mission.MissionStatus = "FAILED";
                }
                assignment.Mission.EndExecutionTime ??= now;
                assignment.Mission.FailureReason ??= request.Reason.Trim();
                db.RobotStatusHistories.Add(new RobotStatusHistory { RobotId = assignment.RobotId,
                    PreviousOperationalStatus = robot!.OperationalStatus, NewOperationalStatus = "MAINTENANCE",
                    Reason = request.Reason.Trim(), ChangedAt = now });
                robot!.OperationalStatus = "MAINTENANCE";
                robot.UpdatedAt = now;
                db.TaskStatusHistories.Add(new TaskStatusHistory { TaskId = taskId,
                    PreviousStatus = task.TaskStatus, NewStatus = "QUEUED", ChangedBy = userId,
                    Reason = $"Recovered load at original pickup; redispatch after 30 seconds. {request.Reason.Trim()}", ChangedAt = now });
                task.TaskStatus = "QUEUED";
                task.QueuedAt = now;
                var retryAt = now.AddSeconds(30);
                if (!task.ScheduledTime.HasValue || task.ScheduledTime < retryAt) task.ScheduledTime = retryAt;
                task.AssignedAt = null;
                task.StartedAt = null;
                task.CompletedAt = null;
                // The old mission retains failure details; the new attempt starts clean.
                task.FailureReason = null;
                control.State = "NORMAL";
                control.Paused = false;
                command = "FORCE_CANCEL"; // Stops/fences the old robot; never authorizes its reuse.
                break;
            case "cancel":
                if (assignment != null)
                {
                    if (mission == null)
                        throw new InvalidOperationException("Mission state is unknown. Request Maintenance cancellation.");
                    var pickup = await db.DeliveryVerifications.AnyAsync(v =>
                        v.MissionId == mission!.MissionId && v.VerificationStage == "PICKUP" && v.IsVerified, ct);
                    // QR demo location counts conservatively as potentially loaded.
                    pickup |= mission != null && routes.GetMission(mission.MissionId)?.PickupReached == true;
                    // After restart a missing demo route snapshot is UNKNOWN, not proof of no load.
                    if (configuration.GetValue<bool>("DemoRoute:Enabled") && mission?.MissionStatus == "NAVIGATING_TO_PICKUP" && routes.GetMission(mission.MissionId) == null)
                    {
                        var execution = await db.MissionExecutions.AsNoTracking().SingleOrDefaultAsync(e => e.MissionId == mission.MissionId, ct);
                        if (execution == null) pickup = true; // Legacy/unknown route is never proof of no load.
                        else
                        {
                            using var payload = JsonDocument.Parse(execution.RoutePayloadJson);
                            pickup |= execution.NextWaypointIndex > payload.RootElement.GetProperty("pickupWaypointIndex").GetInt32();
                        }
                    }
                    var unsafeDevice = await db.RobotIncidents.AnyAsync(i => i.RobotId == robot!.RobotId &&
                        !i.IsResolved && (i.Severity == "ERROR" || i.Severity == "FATAL" || i.Severity == "HIGH" || i.Severity == "CRITICAL"), ct);
                    var maintenance = await db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == robot!.RobotId &&
                        (m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS"), ct);
                    var fresh = robot!.LastTelemetryTime.HasValue &&
                        robot.LastTelemetryTime >= now.AddSeconds(-30);
                    if (!CancellationPolicy.CanDirectCancel(mission?.MissionStatus ?? "UNKNOWN", pickup,
                        robot.ConnectionStatus == "ONLINE" && robot.OperationalStatus == "BUSY" &&
                        !unsafeDevice && !maintenance && !control.Paused, fresh))
                        throw new InvalidOperationException("Direct cancel is unsafe or load state is unknown. Request Maintenance cancellation.");
                    if (!configuration.GetValue<bool>("Mqtt:EnableWorkers"))
                        throw new InvalidOperationException("MQTT workers must be enabled for robot cancellation.");
                    control.State = "CANCEL_STOP_PENDING";
                    command = "CANCEL_STOP";
                }
                else
                {
                    if (task.TaskStatus is not ("PENDING" or "QUEUED" or "CRITICAL"))
                        throw new InvalidOperationException("Operational task requires Maintenance recovery.");
                    control.State = "CANCELLED_HOME";
                }
                CancelTask(task, request.Reason.Trim(), userId, now);
                break;
            case "request-cancel":
                if (assignment == null) throw new InvalidOperationException("No active assignment; use direct cancellation.");
                control.State = "CANCEL_REQUESTED";
                control.Paused = true;
                command = "PAUSE_TASK";
                break;
            case "force-cancel":
                if (control.State != "CANCEL_REQUESTED" || assignment == null)
                    throw new InvalidOperationException("An Operator cancel request with an active assignment is required.");
                if (!request.PhysicalSafetyConfirmed || !request.LoadRemovedConfirmed)
                    throw new InvalidOperationException("Maintenance must confirm physical stop and load removal.");
                CancelTask(task, request.Reason.Trim(), userId, now);
                FinishAssignment(assignment, "MAINTENANCE", userId, request.Reason.Trim(), now);
                control.State = "CANCELLED_HOLD";
                control.Paused = true;
                command = "FORCE_CANCEL";
                break;
            case "pause":
                if (assignment == null) throw new InvalidOperationException("No active assignment for this task.");
                if (control.Paused && control.State=="NORMAL" && control.CommandAcknowledged && control.PauseContextJson!=null)
                    return new {message="Task is already paused. Use Resume after checking safety."};
                control.Paused = true;
                control.PauseContextJson=null;
                control.ResumeFailureReason=null;
                command = "PAUSE_TASK";
                break;
            default: throw new ArgumentException("Unknown intervention action.");
        }
        control.UpdatedAt = now;
        if (assignment != null && command != null) Queue(control, assignment, command, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { taskId, taskStatus = task.TaskStatus, state = control.State,
            paused = control.Paused, retryAt = task.ScheduledTime,
            message = action == "recover-requeue" ?
                $"Recovered at pickup. Task QUEUED; eligible for {(task.DispatchMode == "AUTO" ? "AUTO" : "manual")} assignment at {task.ScheduledTime:O}. Failed robot remains in Maintenance." :
                action=="resume" ? "Resume requested. Robot remains stopped until its retained mission and safety checks are acknowledged." :
                action=="return-to-charge" ? "Stopped-position validation requested. The empty robot will remain unavailable until its charging return is confirmed." :
                command == null ? "Task cancelled." :
                "Intervention saved. Robot command pending acknowledgement; robot is not available for dispatch." };
    }

    private async Task ValidateResume(TaskAssignment assignment,JsonElement context,DateTime now,CancellationToken ct)
    {
        var robot=assignment.Robot;var mission=assignment.Mission!;
        if (assignment.AssignmentStatus!="ACTIVE" || mission.MissionStatus is not ("INITIALIZING" or "NAVIGATING_TO_PICKUP" or "AT_PICKUP" or "NAVIGATING_TO_DELIVERY" or "AT_DELIVERY") ||
            !await db.TransportTasks.AnyAsync(t=>t.TaskId==assignment.TaskId && (t.TaskStatus=="ASSIGNED"||t.TaskStatus=="EXECUTING"),ct) ||
            !robot.IsActive || robot.ConnectionStatus!="ONLINE" || robot.OperationalStatus!="BUSY" || robot.BatteryPercent is null or <20 ||
            !RobotPresencePolicy.IsFresh(robot,now,configuration) || !configuration.GetValue<bool>("Mqtt:EnableWorkers") ||
            await db.RobotMaintenanceRecords.AnyAsync(m=>m.RobotId==robot.RobotId&&(m.MaintenanceStatus=="SCHEDULED"||m.MaintenanceStatus=="IN_PROGRESS"),ct) ||
            await db.RobotIncidents.AnyAsync(i=>i.RobotId==robot.RobotId&&!i.IsResolved&&(i.Severity=="ERROR"||i.Severity=="FATAL"||i.Severity=="HIGH"||i.Severity=="CRITICAL"),ct) ||
            await db.ChargingJobs.AnyAsync(j=>j.RobotId==robot.RobotId&&j.State!="COMPLETED"&&j.State!="DEPARTING",ct))
            throw new InvalidOperationException("Robot/mission is not safe to resume: check online telemetry, battery, faults and Maintenance state.");
        if (!ValidResumeContext(context) || context.GetProperty("mapRevision").GetInt32()!=routes.CurrentMapRevision)
            throw new InvalidOperationException("Controller pause context or map changed. Maintenance recovery is required.");
        var execution=await db.MissionExecutions.AsNoTracking().SingleOrDefaultAsync(e=>e.MissionId==mission.MissionId,ct);
        if (execution==null || execution.MapRevision!=context.GetProperty("mapRevision").GetInt32())
            throw new InvalidOperationException("Persisted mission progress is unavailable or belongs to a different map.");
        if (configuration.GetValue<bool>("Navigation:Enabled")) {
            if (navigation==null) throw new InvalidOperationException("Navigation safety state is unavailable.");
            if (!navigation.CanResumeMotion(robot.RobotCode,out var reason)) throw new InvalidOperationException(reason);
        }
    }

    private static bool ValidResumeContext(JsonElement c) => c.ValueKind==JsonValueKind.Object && c.GetRawText().Length<=2048 &&
        c.TryGetProperty("resumeToken",out var token) && token.ValueKind==JsonValueKind.String && Guid.TryParse(token.GetString(),out _) &&
        c.TryGetProperty("controllerSessionId",out var session) && session.ValueKind==JsonValueKind.String && Guid.TryParse(session.GetString(),out _) &&
        c.TryGetProperty("mapRevision",out var revision) && revision.TryGetInt32(out _) &&
        c.TryGetProperty("nextWaypointIndex",out var index) && index.TryGetInt32(out var i) && i>=0;

    private async Task<bool> EmptyAndHealthy(TaskAssignment a,CancellationToken ct,bool recovered=false)
    {
        if(a.Mission==null || a.Robot.ConnectionStatus!="ONLINE" || !(a.Robot.OperationalStatus=="BUSY"||recovered&&a.Robot.OperationalStatus=="MAINTENANCE") ||
            !RobotPresencePolicy.IsFresh(a.Robot,DateTime.UtcNow,configuration) ||
            !(a.Mission.MissionStatus is "INITIALIZING" or "NAVIGATING_TO_PICKUP" || recovered&&a.Mission.MissionStatus=="FAILED") ||
            await db.DeliveryVerifications.AnyAsync(v=>v.MissionId==a.Mission.MissionId&&v.VerificationStage=="PICKUP"&&v.IsVerified,ct) ||
            await db.RobotIncidents.AnyAsync(i=>i.RobotId==a.RobotId&&!i.IsResolved&&(i.Severity=="ERROR"||i.Severity=="FATAL"||i.Severity=="HIGH"||i.Severity=="CRITICAL"),ct) ||
            await db.RobotMaintenanceRecords.AnyAsync(m=>m.RobotId==a.RobotId&&(m.MaintenanceStatus=="SCHEDULED"||m.MaintenanceStatus=="IN_PROGRESS"),ct))return false;
        if(routes.GetMission(a.Mission.MissionId)?.PickupReached==true)return false;
        var execution=await db.MissionExecutions.AsNoTracking().SingleOrDefaultAsync(e=>e.MissionId==a.Mission.MissionId,ct);
        if(execution==null)return !configuration.GetValue<bool>("DemoRoute:Enabled")||a.Mission.MissionStatus=="INITIALIZING";
        using var payload=JsonDocument.Parse(execution.RoutePayloadJson);
        return execution.NextWaypointIndex<=payload.RootElement.GetProperty("pickupWaypointIndex").GetInt32();
    }

    private void PrepareReturn(TaskIntervention control,TaskAssignment assignment,int anchor,DateTime now)
    {
        var charging=configuration.GetValue<bool>("Charging:Enabled");
        var home=charging?anchor:configuration.GetValue<int?>("Cancellation:HomeArUcoId")
            ?? throw new InvalidOperationException("No return destination is configured.");
        var route=JsonSerializer.SerializeToNode(routes.BuildHomeRoute(anchor,home,assignment.Robot.RobotCode),ChargingService.Json)!.AsObject();
        route["chargeAfterHome"]=charging;
        route["recoveredAssignment"]=assignment.AssignmentStatus=="FAILED";
        control.Paused=false;control.State="RETURNING_HOME";
        Queue(control,assignment,"RETURN_HOME",now,route);
    }

    private void CancelTask(TransportTask task, string reason, int? userId, DateTime now)
    {
        if (task.TaskStatus == "CANCELLED") return;
        db.TaskStatusHistories.Add(new TaskStatusHistory { TaskId = task.TaskId,
            PreviousStatus = task.TaskStatus, NewStatus = "CANCELLED", ChangedBy = userId,
            Reason = reason, ChangedAt = now });
        task.TaskStatus = "CANCELLED";
        task.CancelledAt = now;
        task.CancellationReason = reason;
    }

    private void FinishAssignment(TaskAssignment a, string robotStatus, int? userId, string reason, DateTime now)
    {
        a.AssignmentStatus = "CANCELLED";
        a.EndedAt = now;
        if (a.Mission != null)
        {
            db.MissionStatusHistories.Add(new MissionStatusHistory { MissionId = a.Mission.MissionId,
                PreviousStatus = a.Mission.MissionStatus, NewStatus = "ABORTED", Source = "SYSTEM",
                ChangedBy = userId, Reason = reason, ChangedAt = now });
            a.Mission.MissionStatus = "ABORTED";
            a.Mission.EndExecutionTime = now;
        }
        db.RobotStatusHistories.Add(new RobotStatusHistory { RobotId = a.RobotId,
            PreviousOperationalStatus = a.Robot.OperationalStatus, NewOperationalStatus = robotStatus,
            Reason = reason, ChangedAt = now });
        a.Robot.OperationalStatus = robotStatus;
        a.Robot.UpdatedAt = now;
    }

    private void Audit(string action, long taskId, int? userId, string reason,
        TaskAssignment? a, TaskIntervention control, DateTime now, InterventionRequest? confirmation = null) =>
        db.SystemAuditLogs.Add(new SystemAuditLog { UserId = userId, ActionCategory = "TASK_SAFETY",
            ActionName = action, EntityType = "TransportTask", EntityId = taskId.ToString(), CreatedAt = now,
            Description = JsonSerializer.Serialize(new { reason, taskId, timestamp = now,
                assignmentId = a?.AssignmentId, robotId = a?.RobotId, robotState = a?.Robot.OperationalStatus,
                connection = a?.Robot.ConnectionStatus, missionState = a?.Mission?.MissionStatus,
                control.State, control.Paused, confirmation?.PhysicalSafetyConfirmed,
                confirmation?.LoadRemovedConfirmed, confirmation?.LoadReturnedToPickupConfirmed,
                confirmation?.ExpectedAssignmentId }) });

    private void Queue(TaskIntervention c, TaskAssignment a, string command, DateTime now, object? route = null)
    {
        c.CommandId = Guid.NewGuid().ToString();
        c.Generation++;
        c.CommandAcknowledged = false;
        db.RobotCommandOutbox.Add(new RobotCommandOutbox { AssignmentId = a.AssignmentId,
            Topic = $"smartfleet/robot/{a.Robot.RobotCode}/command", CreatedAt = now,
            Payload = JsonSerializer.Serialize(new { command, commandId = c.CommandId, generation = c.Generation,
                taskId = a.TaskId, assignmentId = a.AssignmentId, missionId = a.Mission?.MissionId, route },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
    }

    public async Task<MissionEventResult> HandleEvent(string code, RobotMissionEvent e, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var a = await db.TaskAssignments.Include(a => a.Robot).Include(a => a.Mission)
            .SingleOrDefaultAsync(a => a.AssignmentId == e.AssignmentId && a.TaskId == e.TaskId &&
                a.Robot.RobotCode == code, ct);
        var c = await db.TaskInterventions.SingleOrDefaultAsync(x => x.TaskId == e.TaskId, ct);
        if (a?.Mission == null || a.Mission.MissionId != e.MissionId || c == null ||
            string.IsNullOrEmpty(e.CommandId) || c.CommandId != e.CommandId)
            return MissionEventResult.Rejected;
        var commands = await db.RobotCommandOutbox.Where(x => x.AssignmentId == a.AssignmentId).ToListAsync(ct);
        var command = commands.SingleOrDefault(x => JsonDocument.Parse(x.Payload).RootElement
            .TryGetProperty("commandId", out var id) && id.GetString() == e.CommandId);
        if (command == null) return MissionEventResult.Rejected;
        if (command.DeliveredAt.HasValue) return MissionEventResult.Duplicate;
        var now = DateTime.UtcNow;
        var sent = JsonDocument.Parse(command.Payload).RootElement.GetProperty("command").GetString();
        if (e.Event == "CONTROL_STOPPED" && sent is "PAUSE_TASK" or "FORCE_CANCEL" or "CANCEL_STOP")
        {
            command.DeliveredAt = now;
            c.CommandAcknowledged = true;
            c.PauseContextJson=sent=="PAUSE_TASK" && c.State=="NORMAL" && c.Paused && e.ResumeContext is {} context && ValidResumeContext(context)
                ? context.GetRawText() : null;
            if (sent == "CANCEL_STOP")
            {
                c.State = "CANCELLED_HOLD"; // No pose/route => retain ACTIVE assignment.
                if (!c.Paused && configuration.GetValue<bool>("DemoRoute:Enabled") && e.CurrentArUcoId.HasValue &&
                    await EmptyAndHealthy(a,ct))
                {
                    try
                    {
                        PrepareReturn(c,a,e.CurrentArUcoId.Value,now);
                    }
                    catch (InvalidOperationException) { /* Physical recovery is required. */ }
                }
            }
        }
        else if (e.Event is "CONTROL_RESUMED" or "CONTROL_RESUME_REJECTED" && sent=="RESUME_TASK" && c.State=="NORMAL" && c.Paused && a.AssignmentStatus=="ACTIVE")
        {
            if (e.Event=="CONTROL_RESUMED") {
                if (c.PauseContextJson==null || e.ResumeContext is not {} resumed || !ValidResumeContext(resumed)) return MissionEventResult.Rejected;
                using var stored=JsonDocument.Parse(c.PauseContextJson);
                if (stored.RootElement.GetProperty("resumeToken").GetString()!=resumed.GetProperty("resumeToken").GetString() ||
                    stored.RootElement.GetProperty("controllerSessionId").GetString()!=resumed.GetProperty("controllerSessionId").GetString()) return MissionEventResult.Rejected;
                await ValidateResume(a,resumed,now,ct);
                c.Paused=false;c.PauseContextJson=null;c.ResumedAt=now;
            }
            command.DeliveredAt=now;c.CommandAcknowledged=true;
            c.ResumeFailureReason=e.Event=="CONTROL_RESUME_REJECTED" ? (e.Reason??"Robot could not validate a safe resume.")[..Math.Min((e.Reason??"Robot could not validate a safe resume.").Length,400)] : null;
            if (e.Event=="CONTROL_RESUME_REJECTED" && e.Reason=="RESUME_CONTEXT_LOST") c.PauseContextJson=null;
        }
        else if (e.Event == "HOME_REACHED" && sent == "RETURN_HOME" && c.State == "RETURNING_HOME" &&
            !c.Paused && e.CurrentArUcoId == JsonDocument.Parse(command.Payload).RootElement.GetProperty("route").GetProperty("homeArUcoId").GetInt32())
        {
            command.DeliveredAt = now;
            c.CommandAcknowledged = true;
            // Keep robot unavailable if health changed on the return journey.
            var recovering=JsonDocument.Parse(command.Payload).RootElement.GetProperty("route").TryGetProperty("recoveredAssignment",out var wasRecovered)&&wasRecovered.GetBoolean();
            var fault = a.Robot.ConnectionStatus != "ONLINE" ||
                !(a.Robot.OperationalStatus=="BUSY" || recovering&&a.Robot.OperationalStatus=="MAINTENANCE") || await db.RobotIncidents.AnyAsync(i =>
                    i.RobotId == a.RobotId && !i.IsResolved && (i.Severity == "ERROR" || i.Severity == "FATAL" || i.Severity == "HIGH" || i.Severity == "CRITICAL"), ct) ||
                await db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == a.RobotId &&
                    (m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS"), ct);
            var charging=configuration.GetValue<bool>("Charging:Enabled") &&
                JsonDocument.Parse(command.Payload).RootElement.GetProperty("route").TryGetProperty("chargeAfterHome",out var handoff) && handoff.GetBoolean();
            if(recovering) {
                if(a.AssignmentStatus!="FAILED" || !await db.TransportTasks.AnyAsync(t=>t.TaskId==a.TaskId&&t.TaskStatus=="QUEUED",ct))return MissionEventResult.Rejected;
                var status=fault?"MAINTENANCE":"BUSY";
                db.RobotStatusHistories.Add(new(){RobotId=a.RobotId,PreviousOperationalStatus=a.Robot.OperationalStatus,NewOperationalStatus=status,
                    Reason="Maintenance confirmed empty recovered robot at camera checkpoint; charging return required",ChangedAt=now});
                a.Robot.OperationalStatus=status;a.Robot.UpdatedAt=now;
            } else FinishAssignment(a, fault ? "MAINTENANCE" : charging?"BUSY":"AVAILABLE", null, "Robot confirmed stopped after cancellation.", now);
            if (configuration.GetValue<bool>("Charging:Enabled") && e.CurrentArUcoId is int clearedMarker)
                await ChargingService.FinishCancelledDeparture(db, a.RobotId, clearedMarker, ct);
            if(charging&&!fault) {
                // The task's last READY may describe its old departure point.
                // Wait for a new idle-camera handshake before freezing a charge route.
                routes.RequireFreshLocalization(code);
                await ChargingService.QueueAfterDelivery(db,a.Robot,a.Mission.MissionId,ct);
            }
            c.State = recovering?"NORMAL":charging&&!fault?"CANCELLED_CHARGING":"CANCELLED_HOME";
        }
        else return MissionEventResult.Rejected;
        c.UpdatedAt = now;
        Audit(e.Event, e.TaskId, null, "Robot safety acknowledgement", a, c, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MissionEventResult.Applied;
    }
}
