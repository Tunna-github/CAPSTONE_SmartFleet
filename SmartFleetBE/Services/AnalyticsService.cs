using System.Data;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;

namespace SmartFleetBE.Services;

public sealed record ReportWindow(DateOnly From, DateOnly To, DateTime StartUtc, DateTime EndUtc, DateTime GeneratedAt,
    int? WarehouseId, int? RobotId, string TimeZone = "Asia/Ho_Chi_Minh (UTC+07)");
public sealed record FleetMetrics(int Registered, int Active, int Online, int Offline, int Available, int Assigned, int Maintenance, int LowBattery, int UnknownBattery);
public sealed record TaskMetrics(int Created, int Completed, int Failed, int Cancelled, int Other, int CompletedInPeriod,
    int LiveQueue, int LiveExecuting, double? SuccessRatePercent, double? AverageCreateToAssignmentSeconds,
    double? AverageCompletionSeconds, int CompletionDurationSamples, IReadOnlyDictionary<string,int> StatusCounts);
public sealed record MissionMetrics(int Created, int Completed, int Failed, int Cancelled, int FailedWithActiveHold,
    double? AverageExecutionSeconds, int ExecutionDurationSamples);
public sealed record MaintenanceMetrics(int Created, int CompletedInPeriod, int OpenNow, int IncidentsCreated, int UnresolvedIncidentsNow);
public sealed record RobotUtilization(int RobotId, string RobotCode, int WarehouseId, bool IsActive, int Assignments,
    int CompletedMissions, double AssignedSeconds, double RegisteredSeconds, double? UtilizationPercent);
public sealed record DailyMetrics(DateOnly Date, int CreatedTasks, int CompletedTasks);
public sealed record WarehouseOption(int WarehouseId, string Code, string Name);
public sealed record RobotOption(int RobotId, int WarehouseId, string RobotCode);
public sealed record AnalyticsReport(ReportWindow Window, FleetMetrics Fleet, TaskMetrics Tasks, MissionMetrics Missions,
    MaintenanceMetrics Maintenance, double? AssignmentUtilizationPercent, IReadOnlyList<RobotUtilization> RobotUtilization,
    IReadOnlyList<DailyMetrics> Daily, IReadOnlyList<WarehouseOption> Warehouses, IReadOnlyList<RobotOption> Robots);

public sealed class AnalyticsService(SmartFleetDbContext db, IConfiguration configuration)
{
    public static ReportWindow Window(DateOnly? from, DateOnly? to, int? warehouse, int? robot, DateTime now)
    {
        var today = DateOnly.FromDateTime(now.AddHours(7)); var end = to ?? today;
        if (end.Year < 1970) throw new ArgumentException("Report dates must start in 1970 or later.");
        var start = from ?? DateOnly.FromDayNumber(Math.Max(end.DayNumber - 6, new DateOnly(1970,1,1).DayNumber));
        if (start.Year < 1970 || start > end || end > today || end.DayNumber - start.DayNumber > 365 || warehouse is <= 0 || robot is <= 0)
            throw new ArgumentException("Choose an ordered date range of at most 366 days, ending no later than today in UTC+07, and positive warehouse/robot IDs.");
        DateTime Utc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddHours(-7), DateTimeKind.Utc);
        return new(start, end, Utc(start), Utc(end.AddDays(1)), now, warehouse, robot);
    }

    public async Task<AnalyticsReport> Read(DateOnly? from, DateOnly? to, int? warehouseId, int? robotId, CancellationToken ct,
        DateTime? clock = null)
    {
        var window = Window(from, to, warehouseId, robotId, clock ?? DateTime.UtcNow);
        var start = window.StartUtc; var end = window.EndUtc; var now = window.GeneratedAt;
        // All aggregates in a response observe the same PostgreSQL snapshot.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var warehouses = await db.Warehouses.AsNoTracking().OrderBy(w => w.WarehouseCode)
            .Select(w => new WarehouseOption(w.WarehouseId, w.WarehouseCode, w.WarehouseName)).ToListAsync(ct);
        if (warehouseId.HasValue && !warehouses.Any(w => w.WarehouseId == warehouseId)) throw new ArgumentException("Warehouse not found.");
        var allRobots = await db.Robots.AsNoTracking().OrderBy(r => r.RobotCode).ToListAsync(ct);
        if (robotId.HasValue && !allRobots.Any(r => r.RobotId == robotId && (!warehouseId.HasValue || r.WarehouseId == warehouseId)))
            throw new ArgumentException("Robot does not belong to the selected warehouse.");
        var robots = allRobots.Where(r => (!warehouseId.HasValue || r.WarehouseId == warehouseId) && (!robotId.HasValue || r.RobotId == robotId)).ToList();
        var ids = robots.Select(r => r.RobotId).ToList();
        var taskQuery = db.TransportTasks.AsNoTracking().Where(t => (!warehouseId.HasValue || t.WarehouseId == warehouseId) &&
            (!robotId.HasValue || t.TaskAssignments.Any(a => a.RobotId == robotId)));
        var tasks = await taskQuery.Where(t => t.CreatedAt >= start && t.CreatedAt < end).ToListAsync(ct);
        var completed = await taskQuery.Where(t => t.TaskStatus == "COMPLETED" && t.CompletedAt >= start && t.CompletedAt < end).ToListAsync(ct);
        var liveQueue = await taskQuery.CountAsync(t => t.TaskStatus == "QUEUED" || t.TaskStatus == "PENDING" || t.TaskStatus == "CRITICAL" || t.TaskStatus == "REDISPATCHING", ct);
        var liveExecuting = await taskQuery.CountAsync(t => t.TaskStatus == "ASSIGNED" || t.TaskStatus == "EXECUTING", ct);
        var assignedIds = await db.TaskAssignments.AsNoTracking().Where(a => ids.Contains(a.RobotId) && a.AssignmentStatus == "ACTIVE").Select(a => a.RobotId).Distinct().ToListAsync(ct);
        var assignments = await db.TaskAssignments.AsNoTracking().Where(a => ids.Contains(a.RobotId) && a.AssignedAt < end &&
            (a.EndedAt >= start || a.EndedAt == null && a.AssignmentStatus == "ACTIVE")).ToListAsync(ct);
        var missionQuery = db.Missions.AsNoTracking().Where(m => ids.Contains(m.Assignment.RobotId));
        var missions = await missionQuery.Where(m => m.CreatedAt >= start && m.CreatedAt < end)
            .Select(m => new { m.MissionStatus, m.StartExecutionTime, m.EndExecutionTime, m.Assignment.AssignmentStatus }).ToListAsync(ct);
        var completedByRobot = await missionQuery.Where(m => m.MissionStatus == "COMPLETED" && m.EndExecutionTime >= start && m.EndExecutionTime < end)
            .GroupBy(m => m.Assignment.RobotId).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct);
        var maintenanceQuery = db.RobotMaintenanceRecords.AsNoTracking().Where(m => ids.Contains(m.RobotId));
        var maintenance = new MaintenanceMetrics(
            await maintenanceQuery.CountAsync(m => m.CreatedAt >= start && m.CreatedAt < end, ct),
            await maintenanceQuery.CountAsync(m => m.MaintenanceStatus == "COMPLETED" && m.CompletedAt >= start && m.CompletedAt < end, ct),
            await maintenanceQuery.CountAsync(m => m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS", ct),
            await db.RobotIncidents.CountAsync(i => ids.Contains(i.RobotId) && i.CreatedAt >= start && i.CreatedAt < end, ct),
            await db.RobotIncidents.CountAsync(i => ids.Contains(i.RobotId) && !i.IsResolved, ct));
        var utilization = robots.Select(r => {
            var robotStart = r.CreatedAt > start ? r.CreatedAt : start;
            var robotEnd = now < end ? now : end;
            var denominator = Math.Max(0, (robotEnd - robotStart).TotalSeconds);
            var history = assignments.Where(a => a.RobotId == r.RobotId).ToList();
            var intervals = history.Select(a => (Start: a.AssignedAt > robotStart ? a.AssignedAt : robotStart,
                End: a.EndedAt is { } ended && ended < robotEnd ? ended : robotEnd)).Where(p => p.End > p.Start).ToList();
            var seconds = UnionSeconds(intervals);
            return new RobotUtilization(r.RobotId, r.RobotCode, r.WarehouseId, r.IsActive, history.Count,
                completedByRobot.SingleOrDefault(c => c.Id == r.RobotId)?.Count ?? 0, seconds, denominator,
                denominator > 0 ? Math.Round(seconds * 100 / denominator, 2) : null);
        }).ToList();
        bool Online(Robot r) => r.IsActive && r.ConnectionStatus == "ONLINE" && RobotPresencePolicy.IsFresh(r, now, configuration);
        var maintenanceRobotIds = await maintenanceQuery.Where(m => m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS").Select(m => m.RobotId).Distinct().ToListAsync(ct);
        var incidentRobotIds = await db.RobotIncidents.Where(i => ids.Contains(i.RobotId) && !i.IsResolved &&
            (i.Severity == "ERROR" || i.Severity == "FATAL" || i.Severity == "HIGH" || i.Severity == "CRITICAL"))
            .Select(i => i.RobotId).Distinct().ToListAsync(ct);
        var reservedRobotIds = await db.StationBerths.Where(b => ids.Contains(b.RobotId)).Select(b => b.RobotId).ToListAsync(ct);
        var fleet = new FleetMetrics(robots.Count, robots.Count(r => r.IsActive), robots.Count(Online), robots.Count(r => r.IsActive && !Online(r)),
            robots.Count(r => Online(r) && r.OperationalStatus == "AVAILABLE" && r.BatteryPercent >= 20 && !assignedIds.Contains(r.RobotId) &&
                !maintenanceRobotIds.Contains(r.RobotId) && !incidentRobotIds.Contains(r.RobotId) && !reservedRobotIds.Contains(r.RobotId)),
            assignedIds.Count, robots.Count(r => r.OperationalStatus == "MAINTENANCE" || maintenanceRobotIds.Contains(r.RobotId)),
            robots.Count(r => r.IsActive && r.BatteryPercent < 20), robots.Count(r => r.IsActive && r.BatteryPercent == null));
        var completedCount = tasks.Count(t => t.TaskStatus == "COMPLETED"); var failed = tasks.Count(t => t.TaskStatus == "FAILED"); var cancelled = tasks.Count(t => t.TaskStatus == "CANCELLED");
        var completionDurations = completed.Where(t => t.CompletedAt >= t.CreatedAt).Select(t => (t.CompletedAt!.Value - t.CreatedAt).TotalSeconds).ToList();
        var waits = tasks.Where(t => t.AssignedAt >= t.CreatedAt).Select(t => (t.AssignedAt!.Value - t.CreatedAt).TotalSeconds).ToList();
        var durations = missions.Where(m => m.MissionStatus == "COMPLETED" && m.StartExecutionTime != null && m.EndExecutionTime >= m.StartExecutionTime)
            .Select(m => (m.EndExecutionTime!.Value - m.StartExecutionTime!.Value).TotalSeconds).ToList();
        var statuses = new[] { "PENDING", "QUEUED", "ASSIGNED", "EXECUTING", "CRITICAL", "REDISPATCHING", "COMPLETED", "FAILED", "CANCELLED" }
            .Concat(tasks.Select(t => t.TaskStatus)).Distinct().ToDictionary(status => status, status => tasks.Count(t => t.TaskStatus == status));
        var taskMetrics = new TaskMetrics(tasks.Count, completedCount, failed, cancelled, tasks.Count - completedCount - failed - cancelled, completed.Count,
            liveQueue, liveExecuting, completedCount + failed > 0 ? Math.Round(100d * completedCount / (completedCount + failed), 2) : null,
            Average(waits), Average(completionDurations), completionDurations.Count, statuses);
        var missionMetrics = new MissionMetrics(missions.Count, missions.Count(m => m.MissionStatus == "COMPLETED"), missions.Count(m => m.MissionStatus == "FAILED"),
            missions.Count(m => m.MissionStatus == "CANCELLED"), missions.Count(m => m.MissionStatus == "FAILED" && m.AssignmentStatus == "ACTIVE"), Average(durations), durations.Count);
        var daily = Enumerable.Range(0, window.To.DayNumber - window.From.DayNumber + 1).Select(i => {
            var day = window.From.AddDays(i);
            return new DailyMetrics(day, tasks.Count(t => DateOnly.FromDateTime(t.CreatedAt.AddHours(7)) == day),
                completed.Count(t => DateOnly.FromDateTime(t.CompletedAt!.Value.AddHours(7)) == day));
        }).ToList();
        var totalRegistered = utilization.Sum(r => r.RegisteredSeconds);
        var result = new AnalyticsReport(window, fleet, taskMetrics, missionMetrics, maintenance,
            totalRegistered > 0 ? Math.Round(utilization.Sum(r => r.AssignedSeconds) * 100 / totalRegistered, 2) : null,
            utilization, daily, warehouses, allRobots.Select(r => new RobotOption(r.RobotId, r.WarehouseId, r.RobotCode)).ToList());
        await tx.CommitAsync(ct); return result;
    }
    private static double? Average(List<double> values) => values.Count == 0 ? null : Math.Round(values.Average(), 2);
    public static double UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> ranges)
    {
        DateTime? start = null, end = null; double total = 0;
        foreach (var range in ranges.OrderBy(r => r.Start)) {
            if (range.End <= range.Start) continue;
            if (end == null) { start = range.Start; end = range.End; }
            else if (range.Start <= end) { if (range.End > end) end = range.End; }
            else { total += (end!.Value - start!.Value).TotalSeconds; start = range.Start; end = range.End; }
        }
        return total + (end.HasValue ? (end.Value - start!.Value).TotalSeconds : 0);
    }
}
