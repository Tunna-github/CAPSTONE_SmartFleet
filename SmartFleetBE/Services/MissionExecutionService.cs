using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public sealed record VerificationRequest(string RequestId, string Stage, string Type,
    string? ScannedQr, string? ScannedPackageCode, bool PhysicalTransferConfirmed, string? Note);
public sealed record VerificationResult(bool Accepted, string Result, bool Duplicate = false);
public sealed record ProgressDecision(bool Accepted, string Reason, string Action = "STOP");

public sealed class MissionExecutionService(SmartFleetDbContext db, QrDemoRouteService routes,
    IConfiguration config)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    private static readonly DateTime ProcessStartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
    public int MaxQrAttempts => Math.Clamp(config.GetValue("Verification:MaxQrAttempts", 3), 1, 20);
    private List<MissionExecution>? readExecutions;
    private List<DeliveryVerification>? readVerifications;
    private List<VerificationAttempt>? readAttempts;
    private List<MissionStatusHistory>? readHistory;
    private HashSet<long>? readHeldTasks;

    // Called INSIDE the assignment transaction. No robot receives an unpersisted route.
    public static async Task<string> PrepareAsync(SmartFleetDbContext db, QrDemoRouteService routes,
        TaskAssignment assignment, CancellationToken ct)
    {
        var old = await db.MissionExecutions.SingleOrDefaultAsync(x => x.MissionId == assignment.Mission!.MissionId, ct);
        if (old != null) return old.RoutePayloadJson;
        var task = await db.TransportTasks.Include(t => t.PickupStation).Include(t => t.DeliveryStation)
            .SingleAsync(t => t.TaskId == assignment.TaskId, ct);
        var pickup = task.PickupStation.QrCodePayload?.Trim();
        var delivery = task.DeliveryStation.QrCodePayload?.Trim();
        if (string.IsNullOrWhiteSpace(pickup) || string.IsNullOrWhiteSpace(delivery) || pickup == delivery)
            throw new InvalidOperationException("Configure distinct pickup/delivery station QR payloads before dispatch.");
        var resolved = await TaskLocationPolicy.ResolveRouteAsync(db, task.WarehouseId, task.PickupStationId, task.DeliveryStationId, routes, ct);
        routes.ConfigureTask(task.TaskId, resolved.PickupArUcoId, resolved.DeliveryArUcoId);
        var payload = routes.BuildMqttCommand(assignment);
        var view = routes.GetMission(assignment.Mission!.MissionId)!;
        routes.ForgetMission(view.MissionId); // Publish/cache only committed progress, never a rolled-back assignment.
        if (view.Waypoints[view.PickupWaypointIndex].IsIntersection || view.Waypoints[^1].IsIntersection)
            throw new InvalidOperationException("Pickup/delivery verification stations cannot occupy a reserved intersection.");
        var now = DateTime.UtcNow;
        db.MissionExecutions.Add(new MissionExecution { MissionId = view.MissionId,
            RoutePayloadJson = payload, MapRevision = view.MapRevision,
            ExpectedPickupQr = pickup, ExpectedDeliveryQr = delivery,
            PackageCode = task.PackageCode, UpdatedAt = now });
        using var document = JsonDocument.Parse(payload);
        var scale = document.RootElement.GetProperty("metersPerGridCell").GetDecimal();
        foreach (var pair in view.Waypoints.Select((w, index) => (w, index)))
            db.MissionWaypoints.Add(new MissionWaypoint { MissionId = view.MissionId,
                SequenceNumber = pair.index + 1,
                StationId = pair.index == view.PickupWaypointIndex ? task.PickupStationId :
                    pair.index == view.Waypoints.Count - 1 ? task.DeliveryStationId : null,
                CoordX = -pair.w.Row * scale, CoordY = -pair.w.Col * scale,
                WaypointAction = pair.w.Stage is "PICKUP" or "DELIVERY" ? "WAIT_VERIFICATION" : "PASS_THROUGH" });
        return payload;
    }

    private static QrDemoRouteService.MissionView ReadRoute(MissionExecution execution)
    {
        using var doc = JsonDocument.Parse(execution.RoutePayloadJson);
        var p = doc.RootElement;
        var waypoints = JsonSerializer.Deserialize<List<QrDemoRouteService.QrWaypoint>>(p.GetProperty("waypoints"), Json)!;
        var pickupIndex = p.GetProperty("pickupWaypointIndex").GetInt32();
        return new() { TaskId = p.GetProperty("taskId").GetInt64(), MissionId = execution.MissionId,
            MapRevision = execution.MapRevision, RobotCode = execution.Mission.Assignment.Robot.RobotCode,
            StartArUcoId = p.GetProperty("startArUcoId").GetInt32(),
            PickupArUcoId = p.GetProperty("pickupArUcoId").GetInt32(),
            DeliveryArUcoId = p.GetProperty("deliveryArUcoId").GetInt32(), PickupWaypointIndex = pickupIndex,
            NextWaypointIndex = execution.NextWaypointIndex,
            LastReachedArUcoId = execution.NextWaypointIndex > 0 ? waypoints[execution.NextWaypointIndex-1].ArucoId : null,
            PickupReached = execution.NextWaypointIndex > pickupIndex,
            DeliveryReached = execution.NextWaypointIndex == waypoints.Count, Waypoints = waypoints };
    }

    private IQueryable<MissionExecution> Executions() => db.MissionExecutions
        .Include(x => x.Mission).ThenInclude(m => m.Assignment).ThenInclude(a => a.Robot);

    public async Task<object?> ViewAsync(long id, CancellationToken ct)
    {
        var e = readExecutions != null ? readExecutions.SingleOrDefault(e => e.MissionId == id) :
            await Executions().AsNoTracking().SingleOrDefaultAsync(e => e.MissionId == id, ct);
        if (e == null) return null;
        var route = ReadRoute(e);
        var verifications = readVerifications?.Where(v => v.MissionId == id).ToList() ??
            await db.DeliveryVerifications.AsNoTracking().Where(v => v.MissionId == id).ToListAsync(ct);
        var attemptRows = readAttempts?.Where(v => v.MissionId == id).ToList() ??
            await db.VerificationAttempts.AsNoTracking().Where(v => v.MissionId == id).OrderBy(v => v.Id).ToListAsync(ct);
        var attempts = attemptRows.Select(v => new { v.Id, v.Stage, v.Type, v.Accepted, v.Result, v.Note, v.UserId, v.CreatedAt }).ToList();
        var history = readHistory?.Where(h => h.MissionId == id).ToList() ??
            await db.MissionStatusHistories.AsNoTracking().Where(h => h.MissionId == id).OrderBy(h => h.ChangedAt).ThenBy(h => h.MissionStatusHistoryId).ToListAsync(ct);
        var held = readHeldTasks?.Contains(e.Mission.Assignment.TaskId) ?? await IsHeld(e.Mission.Assignment.TaskId, ct);
        return new { route.TaskId, route.MissionId, e.Mission.AssignmentId, route.MapRevision, route.RobotCode,
            route.StartArUcoId, route.PickupArUcoId, route.DeliveryArUcoId, route.PickupWaypointIndex,
            route.NextWaypointIndex, route.LastReachedArUcoId, route.PickupReached, route.DeliveryReached, route.Waypoints,
            e.Mission.MissionStatus, e.Mission.Assignment.AssignmentStatus,
            PickupVerified = verifications.Any(v => v.VerificationStage == "PICKUP" && v.IsVerified),
            DeliveryVerified = verifications.Any(v => v.VerificationStage == "DELIVERY" && v.IsVerified),
            PackageRequired = !string.IsNullOrEmpty(e.PackageCode), MaxQrAttempts,
            PickupQrAttempts = attempts.Count(a => a.Stage == "PICKUP" && a.Type == "QRCODE" && !a.Accepted),
            DeliveryQrAttempts = attempts.Count(a => a.Stage == "DELIVERY" && a.Type == "QRCODE" && !a.Accepted),
            Held = held, StationPresenceFresh = e.StationPresenceAt > DateTime.UtcNow.AddSeconds(-15) && e.StationPresenceAt >= ProcessStartedAt,
            Verifications = verifications.Select(v => new { v.VerificationStage, v.VerificationType, v.IsVerified, v.VerifiedAt, v.VerifiedBy, v.Note }),
            Attempts = attempts,
            StatusHistory = history.Select(h => new { h.PreviousStatus, h.NewStatus, h.Source, h.ChangedBy, h.Reason, h.ChangedAt }).ToList() };
    }

    public async Task<IReadOnlyList<object>> ViewsAsync(CancellationToken ct)
    {
        // Five bounded queries instead of one round-trip per section per mission.
        readExecutions = await Executions().AsNoTracking().OrderByDescending(e => e.Mission.Assignment.AssignmentStatus == "ACTIVE")
            .ThenByDescending(e => e.MissionId).Take(50).ToListAsync(ct);
        var ids = readExecutions.Select(e => e.MissionId).ToArray();
        var tasks = readExecutions.Select(e => e.Mission.Assignment.TaskId).ToArray();
        readVerifications = await db.DeliveryVerifications.AsNoTracking().Where(v => ids.Contains(v.MissionId)).ToListAsync(ct);
        readAttempts = await db.VerificationAttempts.AsNoTracking().Where(v => ids.Contains(v.MissionId)).OrderBy(v => v.Id).ToListAsync(ct);
        readHistory = await db.MissionStatusHistories.AsNoTracking().Where(h => ids.Contains(h.MissionId)).OrderBy(h => h.ChangedAt).ThenBy(h => h.MissionStatusHistoryId).ToListAsync(ct);
        readHeldTasks = (await db.TaskInterventions.Where(x => tasks.Contains(x.TaskId) && (x.Paused || x.State != "NORMAL")).Select(x => x.TaskId).ToListAsync(ct)).ToHashSet();
        var result = new List<object>();
        foreach (var id in ids) if (await ViewAsync(id, ct) is { } item) result.Add(item);
        return result;
    }

    private Task<bool> IsHeld(long taskId, CancellationToken ct) => db.TaskInterventions.AnyAsync(x => x.TaskId == taskId &&
        (x.Paused || x.State != "NORMAL"), ct);

    public async Task<T> RetryAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
            try { return await operation(); }
            catch (Exception ex) when (attempt < 4 && IsConflict(ex))
            { db.ChangeTracker.Clear(); await Task.Delay(50 * attempt, ct); }
    }

    public static bool IsConflict(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException || current is Npgsql.PostgresException { SqlState: "40001" or "40P01" or "23505" }) return true;
        return false;
    }

    public Task<VerificationResult> VerifyAsync(long id, int userId, VerificationRequest request, CancellationToken ct)
        => RetryAsync(() => VerifyCore(id, userId, request, ct), ct);

    private async Task<VerificationResult> VerifyCore(long id, int userId, VerificationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.RequestId, out _) || request.Stage is not ("PICKUP" or "DELIVERY") ||
            request.Type is not ("QRCODE" or "MANUAL") || request.ScannedQr?.Length > 150 ||
            request.ScannedPackageCode?.Length > 150 || request.Note?.Length > 500 || !request.PhysicalTransferConfirmed)
            throw new ArgumentException("Valid request ID, stage, method and physical transfer confirmation are required; QR <=150 and note <=500 characters.");
        if (request.Type == "MANUAL" && string.IsNullOrWhiteSpace(request.Note))
            throw new ArgumentException("Manual verification requires a reason.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { userId, request }, Json))));
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var previous = await db.VerificationAttempts.SingleOrDefaultAsync(v => v.MissionId == id && v.RequestId == request.RequestId, ct);
        if (previous != null)
        {
            if (previous.RequestHash != hash) throw new InvalidOperationException("Request ID was already used for different verification data.");
            return new(previous.Accepted, previous.Result, true);
        }
        var e = await Executions().SingleOrDefaultAsync(e => e.MissionId == id, ct)
            ?? throw new KeyNotFoundException("Persisted mission not found.");
        var a = e.Mission.Assignment;
        if (a.AssignmentStatus != "ACTIVE" || await IsHeld(a.TaskId, ct))
            throw new InvalidOperationException("Mission is inactive or held for safety.");
        var requiredStatus = request.Stage == "PICKUP" ? "AT_PICKUP" : "AT_DELIVERY";
        if (e.Mission.MissionStatus != requiredStatus || e.StationPresenceAt < DateTime.UtcNow.AddSeconds(-15) ||
            e.StationPresenceAt == null || e.StationPresenceAt < ProcessStartedAt || a.Robot.ConnectionStatus != "ONLINE" || a.Robot.OperationalStatus != "BUSY" ||
            !RobotPresencePolicy.IsFresh(a.Robot, DateTime.UtcNow, config))
            throw new InvalidOperationException("Robot must be stopped at this station with fresh camera/presence and no device fault.");
        if (request.Stage == "DELIVERY" && !await Verified(id, "PICKUP", ct))
            throw new InvalidOperationException("Pickup must be verified first.");
        if (await db.RobotIncidents.AnyAsync(i => i.RobotId == a.RobotId && !i.IsResolved &&
            (i.Severity == "ERROR" || i.Severity == "FATAL" || i.Severity == "HIGH" || i.Severity == "CRITICAL"), ct))
            throw new InvalidOperationException("Resolve the robot safety fault before verification.");
        if (await Verified(id, request.Stage, ct)) throw new InvalidOperationException("This stage is already verified. Refresh the mission.");
        var failures = await db.VerificationAttempts.CountAsync(v => v.MissionId == id && v.Stage == request.Stage && v.Type == "QRCODE" && !v.Accepted, ct);
        if (request.Type == "QRCODE" && failures >= MaxQrAttempts)
            throw new InvalidOperationException("QR retry limit reached. Investigate and use a reasoned manual verification or Maintenance recovery.");
        var expected = request.Stage == "PICKUP" ? e.ExpectedPickupQr : e.ExpectedDeliveryQr;
        var accepted = request.Type == "MANUAL" || request.ScannedQr?.Trim() == expected &&
            (string.IsNullOrEmpty(e.PackageCode) || request.ScannedPackageCode?.Trim() == e.PackageCode);
        var result = accepted ? "VERIFIED" : "QR_OR_PACKAGE_MISMATCH";
        var now = DateTime.UtcNow;
        db.VerificationAttempts.Add(new VerificationAttempt { MissionId = id, RequestId = request.RequestId,
            RequestHash = hash, Stage = request.Stage, Type = request.Type, ScannedQr = request.ScannedQr,
            ScannedPackageCode = request.ScannedPackageCode, ExpectedQr = expected, Accepted = accepted,
            Result = result, Note = request.Note?.Trim(), UserId = userId, CreatedAt = now });
        db.SystemAuditLogs.Add(new SystemAuditLog { UserId = userId, ActionCategory = "VERIFICATION", ActionName = "VERIFY_" + request.Stage,
            EntityType = "Mission", EntityId = id.ToString(),
            Description = $"{request.Stage} {request.Type}: {result}; robot={a.Robot.RobotCode}/{a.Robot.OperationalStatus}; assignment={a.AssignmentId}; reason={request.Note}", CreatedAt = now });
        if (request.Type == "QRCODE") db.RobotQrScanEvents.Add(new RobotQrScanEvent { RobotId = a.RobotId,
            MissionId = id, EventId = $"verify:{id}:{request.RequestId}", QrPayloadScanned = request.ScannedQr?.Trim() ?? "",
            ScanPurpose = request.Stage, IsValid = accepted, InvalidReason = accepted ? null : result, ScannedAt = now });
        if (accepted)
        {
            db.DeliveryVerifications.Add(new DeliveryVerification { MissionId = id, VerificationStage = request.Stage,
                VerificationType = request.Type, ExpectedQrPayload = expected, QrPayloadScanned = request.ScannedQr,
                IsVerified = true, VerifiedAt = now, VerifiedBy = userId, Note = request.Note?.Trim(), CreatedAt = now });
            if (request.Stage == "PICKUP") ChangeStatus(e.Mission, "NAVIGATING_TO_DELIVERY", "OPERATOR", "Pickup physically confirmed.", now, userId);
            // Completion still requires a matching final robot event; never free the robot from a UI POST.
        }
        else await OperationalAlertService.RaiseAsync(db, a.Robot, "VERIFICATION_MISMATCH", "WARNING",
            $"{request.Stage} verification mismatch on mission {id}. Robot remains stopped.", now, a.TaskId, id, ct);
        e.Revision++; e.UpdatedAt = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(accepted, result);
    }

    private Task<bool> Verified(long id, string stage, CancellationToken ct) => db.DeliveryVerifications.AnyAsync(v =>
        v.MissionId == id && v.VerificationStage == stage && v.IsVerified, ct);

    private void ChangeStatus(Mission mission, string status, string source, string reason, DateTime now, int? user = null)
    {
        if (mission.MissionStatus == status) return;
        db.MissionStatusHistories.Add(new MissionStatusHistory { MissionId = mission.MissionId,
            PreviousStatus = mission.MissionStatus, NewStatus = status, Source = source, Reason = reason,
            ChangedBy = user, ChangedAt = now });
        mission.MissionStatus = status; mission.LastProgressAt = now;
    }

    public Task<ProgressDecision> ProgressAsync(string code, QrDemoRouteService.RouteProgressReport report, CancellationToken ct)
        => RetryAsync(() => ProgressCore(code, report, ct), ct);

    private async Task<ProgressDecision> ProgressCore(string code, QrDemoRouteService.RouteProgressReport report, CancellationToken ct)
    {
        if (report.EventId.Length is < 1 or > 100) return new(false, "Invalid event ID.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var e = await Executions().SingleOrDefaultAsync(e => e.MissionId == report.MissionId, ct);
        if (e == null || e.Mission.Assignment.Robot.RobotCode != code) return new(false, "No matching persisted mission.");
        var view = ReadRoute(e);
        var a = e.Mission.Assignment;
        if (a.AssignmentStatus != "ACTIVE" || await IsHeld(a.TaskId, ct) ||
            e.Mission.MissionStatus is "INITIALIZING" or "FAILED" or "CANCELLED" or "ABORTED" or "COMPLETED")
            return new(false, "Mission is not executing or is held for safety.");
        if (a.Robot.OperationalStatus != "BUSY" || a.Robot.ConnectionStatus != "ONLINE" ||
            !RobotPresencePolicy.IsFresh(a.Robot, DateTime.UtcNow, config))
            return new(false, "Robot has no fresh healthy execution state.");
        if (routes.CurrentMapRevision != e.MapRevision || report.MapRevision != e.MapRevision)
            return new(false, "Route/map revision mismatch; stop for recovery.");
        if (report.WaypointIndex < 0 || report.WaypointIndex >= view.Waypoints.Count ||
            view.Waypoints[report.WaypointIndex].ArucoId != report.ArucoId || report.WaypointIndex > e.NextWaypointIndex)
            return new(false, "Unknown, out-of-order or off-route waypoint.");
        var now = DateTime.UtcNow;
        if (report.Event == "WAYPOINT_REACHED")
        {
            if (report.WaypointIndex == e.NextWaypointIndex && report.WaypointIndex > view.PickupWaypointIndex &&
                !await Verified(e.MissionId, "PICKUP", ct))
                return new(false, "Pickup verification required before movement.");
            var previous = await db.RobotQrScanEvents.SingleOrDefaultAsync(s => s.RobotId == a.RobotId && s.EventId == report.EventId, ct);
            if (previous != null && (previous.MissionId != report.MissionId || previous.WaypointIndex != report.WaypointIndex || previous.QrPayloadScanned != $"ARUCO:{report.ArucoId}"))
                return new(false, "Event ID reused for different scan.");
            if (previous == null)
                db.RobotQrScanEvents.Add(new RobotQrScanEvent { RobotId = a.RobotId, MissionId = e.MissionId,
                    EventId = report.EventId, WaypointIndex = report.WaypointIndex, QrPayloadScanned = $"ARUCO:{report.ArucoId}",
                    ScanPurpose = "NAVIGATION", IsValid = true, ScannedAt = now });
            if (report.WaypointIndex == e.NextWaypointIndex)
            {
                e.NextWaypointIndex++;
                var waypoint = await db.MissionWaypoints.SingleAsync(w => w.MissionId == e.MissionId && w.SequenceNumber == report.WaypointIndex + 1, ct);
                waypoint.IsReached = true; waypoint.ReachedAt = now;
                e.Mission.LastProgressAt = now;
                if (report.WaypointIndex == view.PickupWaypointIndex) ChangeStatus(e.Mission, "AT_PICKUP", "ROBOT_AGENT", "Camera-confirmed pickup; waiting for physical verification.", now);
                if (report.WaypointIndex == view.Waypoints.Count - 1) ChangeStatus(e.Mission, "AT_DELIVERY", "ROBOT_AGENT", "Camera-confirmed delivery; waiting for physical verification.", now);
            }
        }
        else if (report.Event != "VERIFICATION_STATUS") return new(false, "Unsupported mission progress event.");
        if (report.Event == "VERIFICATION_STATUS" && (!report.RobotStopped || report.WaypointIndex != e.NextWaypointIndex - 1))
            return new(false, "Station status must confirm a stopped robot at its last reached checkpoint.");
        if (report.RobotStopped && report.WaypointIndex == e.NextWaypointIndex - 1 &&
            (report.WaypointIndex == view.PickupWaypointIndex || report.WaypointIndex == view.Waypoints.Count - 1) &&
            (e.StationPresenceAt == null || e.StationPresenceAt < now.AddSeconds(-3)))
            e.StationPresenceAt = now;
        var action = report.WaypointIndex == view.PickupWaypointIndex && !await Verified(e.MissionId, "PICKUP", ct) ? "WAIT_PICKUP_VERIFICATION" :
            report.WaypointIndex == view.Waypoints.Count - 1 ? (await Verified(e.MissionId, "DELIVERY", ct) ? "COMPLETE" : "WAIT_DELIVERY_VERIFICATION") : "CONTINUE";
        if (db.ChangeTracker.HasChanges()) { e.Revision++; e.UpdatedAt = now; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        routes.RestoreMission(ReadRoute(e), e.RoutePayloadJson);
        if (report.WaypointIndex == e.NextWaypointIndex - 1)
            routes.RecordCameraProgress(code, report.ArucoId, report.HeadingDeg, e.MapRevision);
        return new(true, "Progress persisted.", action);
    }
}
