using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed class ChargingService(SmartFleetDbContext db, QrDemoRouteService routes,
    NavigationCoordinator navigation, IConfiguration config)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public bool Enabled => config.GetValue<bool>("Charging:Enabled");

    // Inside the SAME transaction as completed delivery: no AVAILABLE gap.
    public static async Task QueueAfterDelivery(SmartFleetDbContext db, Robot robot, long missionId, CancellationToken ct)
    {
        if (await db.ChargingJobs.AnyAsync(j => j.SourceMissionId == missionId, ct)) return;
        if (await db.ChargingJobs.AnyAsync(j => j.RobotId == robot.RobotId && j.State != "COMPLETED", ct))
            throw new InvalidOperationException("Robot already has an unfinished charging cycle.");
        var generation = (await db.ChargingJobs.Where(j => j.RobotId == robot.RobotId).MaxAsync(j => (int?)j.Generation, ct) ?? 0) + 1;
        db.ChargingJobs.Add(new() { Id = Guid.NewGuid(), RobotId = robot.RobotId, SourceMissionId = missionId,
            Generation = generation, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        robot.OperationalStatus = "BUSY";
    }

    public static async Task BeginDeparture(SmartFleetDbContext db, int robotId, CancellationToken ct, QrDemoRouteService? routes = null)
    {
        var job = await db.ChargingJobs.SingleOrDefaultAsync(j => j.RobotId == robotId && j.State != "COMPLETED", ct);
        if (job == null) return;
        if (job.State != "READY" || job.LastStoppedAt < DateTime.UtcNow.AddSeconds(-8) || job.LastStoppedAt == null ||
            !await db.StationBerths.AnyAsync(b=>b.RobotId==robotId && b.StationId==job.StationId,ct))
            throw new InvalidOperationException("Robot must finish its safe return to a charging berth and report fresh stopped presence before dispatch.");
        if (routes != null) {
            var robot = await db.Robots.SingleAsync(r=>r.RobotId==robotId,ct);
            var marker=await db.StationCheckpoints.Where(p=>p.StationId==job.StationId).Select(p=>p.ArucoId).SingleAsync(ct);
            if(!routes.GetRobotPositions().TryGetValue(robot.RobotCode,out var actual) || actual!=marker)
                throw new InvalidOperationException("Robot is no longer localized at its reserved charging berth.");
        }
        job.State = "DEPARTING"; job.UpdatedAt = DateTime.UtcNow; job.Revision++;
    }

    public static async Task ConfirmDeparture(SmartFleetDbContext db, int robotId, int marker, CancellationToken ct)
    {
        var job = await db.ChargingJobs.SingleOrDefaultAsync(j => j.RobotId == robotId && j.State == "DEPARTING", ct);
        if (job?.StationId == null) return;
        var checkpoint = await db.StationCheckpoints.SingleAsync(p => p.StationId == job.StationId, ct);
        if (marker == checkpoint.ArucoId) return;
        // Called only AFTER ordered, persisted task waypoint progress validation.
        var berth = await db.StationBerths.SingleOrDefaultAsync(b => b.RobotId == robotId && b.StationId == job.StationId, ct);
        if (berth == null) throw new InvalidOperationException("Charging departure lost its berth ownership.");
        db.StationBerths.Remove(berth);
        job.State = "COMPLETED"; job.UpdatedAt = DateTime.UtcNow; job.Revision++;
    }

    private Task<bool> Unsafe(int id, CancellationToken ct) => db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == id &&
        (m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS"), ct);

    public async Task ReleaseRecoveredDeparture(Guid id,int actor,string reason,bool physicallyChecked,CancellationToken ct)
    {
        if(!Enabled || !physicallyChecked || reason.Trim().Length is < 5 or > 500)
            throw new ArgumentException("Maintenance inspection and a reason are required before releasing a vacated bay.");
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);
        var job=await db.ChargingJobs.Include(j=>j.Robot).SingleOrDefaultAsync(j=>j.Id==id,ct) ?? throw new KeyNotFoundException();
        if(job.State!="DEPARTING" || await db.TaskAssignments.AnyAsync(a=>a.RobotId==job.RobotId && a.AssignmentStatus=="ACTIVE",ct) ||
            !routes.GetRobotPositions().TryGetValue(job.Robot.RobotCode,out var current))
            throw new InvalidOperationException("Finish task/load recovery and obtain a fresh camera checkpoint outside the charging bay first.");
        var marker=await db.StationCheckpoints.Where(p=>p.StationId==job.StationId).Select(p=>p.ArucoId).SingleAsync(ct);
        if(current==marker) throw new InvalidOperationException("Robot still occupies its charging bay. Keep its reservation.");
        await ConfirmDeparture(db,job.RobotId,current,ct);
        db.SystemAuditLogs.Add(new() {UserId=actor,ActionCategory="SAFETY",ActionName="RELEASE_RECOVERED_CHARGING_BAY",EntityType="ChargingJob",EntityId=id.ToString(),
            Description=JsonSerializer.Serialize(new {job.RobotId,job.Robot.OperationalStatus,marker,current,reason,physicallyChecked}),CreatedAt=DateTime.UtcNow});
        // Does not clear task/robot faults, safety latches, or Maintenance status.
        await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);
    }

    private Task<bool> SeriousIncident(int id, CancellationToken ct) => db.RobotIncidents.AnyAsync(i => i.RobotId == id && !i.IsResolved &&
        (i.Severity == "ERROR" || i.Severity == "FATAL" || i.Severity == "HIGH" || i.Severity == "CRITICAL"), ct);

    public async Task<string?> Tick(Guid id, CancellationToken ct)
    {
        if (!Enabled) return null;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var job = await db.ChargingJobs.Include(j => j.Robot).SingleAsync(j => j.Id == id, ct);
        var robot = job.Robot;
        var previousState = job.State;
        var previousStatus = robot.OperationalStatus;
        var now = DateTime.UtcNow;
        string? command = null;
        if (job.State == "COMPLETED" || job.State == "DEPARTING") return null;
        var healthy = robot.IsActive && robot.ConnectionStatus == "ONLINE" && RobotPresencePolicy.IsFresh(robot, now, config);
        if (job.State is "RETURNING" or "DOCKED" or "READY" && (!healthy || job.State == "RETURNING" && robot.BatteryPercent < 10 || await Unsafe(robot.RobotId, ct) || await SeriousIncident(robot.RobotId, ct)))
        {
            job.State = "FAILED"; job.Reason = "Charging return interrupted by offline/device safety condition; berth and corridor retained.";
            job.StopAcknowledgedAt=null;
            robot.OperationalStatus = "ERROR";
            await OperationalAlertService.RaiseAsync(db, robot, "CHARGING_FAILED", "ERROR", job.Reason, now, null, job.SourceMissionId, ct);
        }
        if (job.State == "WAITING" && healthy && robot.OperationalStatus == "BUSY" && !await Unsafe(robot.RobotId, ct) && !await SeriousIncident(robot.RobotId, ct))
        {
            if (await db.TaskAssignments.AnyAsync(a => a.RobotId == robot.RobotId && a.AssignmentStatus == "ACTIVE", ct)) return null;
            var position = routes.GetRobotPositions();
            if (!position.TryGetValue(robot.RobotCode, out var start)) return null;
            var stations = await db.Stations.Include(s => s.Checkpoint).Include(s => s.Zone).ThenInclude(z => z.Warehouse)
                .Where(s => s.StationType == "CHARGING" && s.Zone.WarehouseId == robot.WarehouseId).OrderBy(s => s.StationId).ToListAsync(ct);
            foreach (var station in stations)
            {
                if (job.StationId != null && job.StationId != station.StationId) continue;
                if (station.IsOccupied || StationCatalogService.UnavailableReason(station, navigation.GetMap(), navigation.WarehouseId) != null ||
                    await db.StationBerths.AnyAsync(b => b.StationId == station.StationId && b.RobotId != robot.RobotId, ct)) continue;
                object route;
                try { route = routes.BuildHomeRoute(start, station.Checkpoint!.ArucoId, robot.RobotCode); }
                catch (InvalidOperationException) { continue; } // wait outside circulation, no phantom route
                if (!await db.StationBerths.AnyAsync(b => b.RobotId == robot.RobotId, ct))
                    db.StationBerths.Add(new() { StationId = station.StationId, RobotId = robot.RobotId, ReservedAt = now });
                job.StationId = station.StationId; job.MapRevision = routes.CurrentMapRevision;
                job.RouteJson = JsonSerializer.Serialize(new { command = "CHARGE_ROUTE", chargingJobId = job.Id,
                    generation = job.Generation, route }, Json);
                job.NextWaypointIndex = 0; job.LastEventId = null; job.State = "RETURNING";
                job.Reason = null;
                break;
            }
        }
        if (job.State is "DOCKED" or "READY")
        {
            var berthQr = await db.StationCheckpoints.Where(p => p.StationId == job.StationId).Select(p => p.ArucoId).SingleAsync(ct);
            var localized = routes.GetRobotPositions().TryGetValue(robot.RobotCode, out var qr) && qr == berthQr;
            var canDepart = healthy && localized && job.LastStoppedAt > now.AddSeconds(-15) && robot.BatteryPercent >= 20 &&
                !await Unsafe(robot.RobotId, ct) && !await SeriousIncident(robot.RobotId, ct);
            job.State = canDepart ? "READY" : "DOCKED";
            robot.OperationalStatus = canDepart ? "AVAILABLE" : "CHARGING";
        }
        if (job.State == "RETURNING") command = job.RouteJson;
        if (job.State is "PAUSED" or "FAILED") command = JsonSerializer.Serialize(new {
            command = "CHARGE_STOP", chargingJobId = job.Id, generation = job.Generation, reason = job.Reason }, Json);
        if (previousState != job.State) db.SystemAuditLogs.Add(new() { ActionCategory="DEVICE",ActionName="CHARGING_STATE",EntityType="ChargingJob",EntityId=job.Id.ToString(),
            Description=JsonSerializer.Serialize(new {job.RobotId,previousState,nextState=job.State,job.StationId,job.Generation,job.Reason}),CreatedAt=now });
        if (previousStatus != robot.OperationalStatus) db.RobotStatusHistories.Add(new() {RobotId=robot.RobotId,PreviousOperationalStatus=previousStatus,
            NewOperationalStatus=robot.OperationalStatus,Reason=$"Charging cycle {job.Id}: {job.State}",ChangedAt=now});
        if (db.ChangeTracker.HasChanges()) {
            job.UpdatedAt = now; job.Revision++;
            if(previousStatus != robot.OperationalStatus) robot.UpdatedAt=now;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return command;
    }

    public async Task<ProgressDecision> Progress(string code, QrDemoRouteService.RouteProgressReport report, CancellationToken ct)
    {
        if (!Enabled || report.ChargingJobId == null || !Guid.TryParse(report.EventId, out _)) return new(false,"Invalid charging report.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var j = await db.ChargingJobs.Include(j => j.Robot).SingleOrDefaultAsync(j => j.Id == report.ChargingJobId, ct);
        if (j == null || j.Robot.RobotCode != code || report.Generation != j.Generation || j.State == "COMPLETED") return new(false,"Charging cycle/generation mismatch.");
        var now = DateTime.UtcNow;
        if (report.Event == "CHARGE_STOPPED")
        {
            if (j.State is not ("PAUSED" or "FAILED") || !report.RobotStopped) return new(false,"No matching stopped safety hold.");
            j.StopAcknowledgedAt = now; j.Revision++;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true,"Charging stop acknowledged.");
        }
        if (report.Event == "CHARGE_FAILED")
        {
            if(j.State == "PAUSED") return new(true,"Explicit pause remains held.");
            if(j.State != "FAILED") j.StopAcknowledgedAt=null;
            j.State = "FAILED"; var failure = report.Reason ?? "Robot charging navigation halted."; j.Reason = failure[..Math.Min(failure.Length,450)];
            j.Robot.OperationalStatus = "ERROR";
            await OperationalAlertService.RaiseAsync(db,j.Robot,"CHARGING_FAILED","ERROR",j.Reason,now,null,j.SourceMissionId,ct);
        }
        else
        {
            if (j.State is not ("RETURNING" or "DOCKED" or "READY") || j.RouteJson == null || !report.RobotStopped ||
                report.MapRevision != routes.CurrentMapRevision || j.MapRevision != report.MapRevision || report.HeadingDeg is not { } heading ||
                !double.IsFinite(heading) || heading is < -180 or > 180 || j.Robot.ConnectionStatus != "ONLINE" ||
                !RobotPresencePolicy.IsFresh(j.Robot, now, config) || await Unsafe(j.RobotId,ct) || await SeriousIncident(j.RobotId,ct))
                return new(false,"Charging route is held or has no fresh stopped camera report.");
            using var d = JsonDocument.Parse(j.RouteJson);
            var points = d.RootElement.GetProperty("route").GetProperty("waypoints");
            if (report.WaypointIndex < 0 || report.WaypointIndex >= points.GetArrayLength() || report.ArucoId != points[report.WaypointIndex].GetProperty("arucoId").GetInt32() ||
                report.WaypointIndex > j.NextWaypointIndex || report.WaypointIndex < j.NextWaypointIndex - 1)
                return new(false,"Unknown or out-of-order charging checkpoint.");
            if (report.Event == "CHARGE_DOCK_STATUS" && (report.WaypointIndex != points.GetArrayLength()-1 || j.NextWaypointIndex != points.GetArrayLength()))
                return new(false,"Dock status requires confirmed final waypoint.");
            if (report.Event is not ("CHARGE_WAYPOINT" or "CHARGE_DOCK_STATUS")) return new(false,"Unsupported charging progress.");
            if (report.WaypointIndex == j.NextWaypointIndex) j.NextWaypointIndex++;
            j.LastEventId = report.EventId;
            if (j.NextWaypointIndex == points.GetArrayLength())
            {
                var justArrived=j.State=="RETURNING";
                j.State = j.State == "READY" ? "READY" : "DOCKED";
                if(report.Event == "CHARGE_DOCK_STATUS") j.LastStoppedAt=now;
                else if(justArrived) j.LastStoppedAt=null; // wait for stopped idle-camera/READY handshake
                j.Robot.OperationalStatus = j.State == "READY" ? "AVAILABLE" : "CHARGING";
            }
        }
        j.UpdatedAt = now; j.Revision++;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        if (report.Event != "CHARGE_FAILED") routes.RecordCameraProgress(code,report.ArucoId,report.HeadingDeg,j.MapRevision);
        return new(true,j.State == "DOCKED" ? "DOCKED" : "Charging progress persisted.",j.State == "RETURNING" ? "CONTINUE" : "STOP");
    }

    public async Task Control(Guid id, bool resume, int actor, string reason, bool physicallyChecked, CancellationToken ct)
    {
        for (var attempt=1;;attempt++) try { await ControlCore(id,resume,actor,reason,physicallyChecked,ct);return; }
        catch(Exception ex) when(attempt<4 && !ct.IsCancellationRequested && MissionExecutionService.IsConflict(ex))
        { db.ChangeTracker.Clear();await Task.Delay(50*attempt,ct); }
    }
    private async Task ControlCore(Guid id, bool resume, int actor, string reason, bool physicallyChecked, CancellationToken ct)
    {
        if (!Enabled || reason.Trim().Length is < 5 or > 500) throw new ArgumentException("A reason (5..500 characters) is required.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var j = await db.ChargingJobs.Include(j=>j.Robot).SingleOrDefaultAsync(j=>j.Id==id,ct) ?? throw new KeyNotFoundException();
        if (j.State is "COMPLETED" or "DEPARTING") throw new InvalidOperationException("Use transport task safety controls after dispatch.");
        if (resume)
        {
            if (j.State is not ("PAUSED" or "FAILED") || j.StopAcknowledgedAt == null || !physicallyChecked || !routes.HasFreshLocalization(j.Robot.RobotCode) ||
                j.Robot.ConnectionStatus != "ONLINE" || !RobotPresencePolicy.IsFresh(j.Robot,DateTime.UtcNow,config) ||
                await Unsafe(j.RobotId,ct) || await SeriousIncident(j.RobotId,ct))
                throw new InvalidOperationException("Maintenance must stop/check the robot, resolve faults, and obtain fresh camera localization before resuming.");
            j.Generation++; j.State="WAITING"; j.RouteJson=null; j.StopAcknowledgedAt=null; j.Robot.OperationalStatus="BUSY";
        }
        else { j.State="PAUSED"; j.Robot.OperationalStatus="ERROR"; j.StopAcknowledgedAt=null; }
        j.Reason=reason.Trim(); j.Revision++; j.UpdatedAt=DateTime.UtcNow;
        db.SystemAuditLogs.Add(new() { UserId=actor,ActionCategory="SAFETY",ActionName=resume?"RESUME_CHARGING":"PAUSE_CHARGING",EntityType="ChargingJob",EntityId=j.Id.ToString(),
            Description=JsonSerializer.Serialize(new {j.RobotId,j.Robot.OperationalStatus,j.Generation,reason,physicallyChecked}),CreatedAt=DateTime.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}

public sealed class ChargingWorker(IServiceScopeFactory scopes, IConfiguration config, MqttService mqtt,
    FleetRealtimePublisher realtime, ILogger<ChargingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!config.GetValue<bool>("Charging:Enabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(token))
        {
          try {
            using var scope = scopes.CreateScope();
            var db=scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
            var jobs=await db.ChargingJobs.AsNoTracking().Where(j=>j.State!="COMPLETED" && j.State!="DEPARTING").Select(j=>new {j.Id,j.Robot.RobotCode}).ToListAsync(token);
            foreach(var job in jobs) try {
                db.ChangeTracker.Clear();
                var payload=await scope.ServiceProvider.GetRequiredService<ChargingService>().Tick(job.Id,token);
                if(payload!=null) await mqtt.PublishAsync($"smartfleet/robot/{job.RobotCode}/command",payload,token);
                await realtime.RobotUpdatedAsync(job.RobotCode,token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { return; }
            catch(Exception ex) { logger.LogWarning(ex,"Charging cycle {Id} remains reserved; retrying",job.Id); }
          }
          catch(OperationCanceledException) when(token.IsCancellationRequested) { return; }
          catch(Exception ex) { logger.LogWarning(ex,"Charging worker database read failed; retaining all reservations and retrying"); }
        }
    }
}
