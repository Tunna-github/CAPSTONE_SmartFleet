using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.DTOs;
using SmartFleetBE.Hubs;

namespace SmartFleetBE.Services;

// Read the committed snapshot in a new DbContext scope before broadcasting.
public sealed class FleetRealtimePublisher(
    IHubContext<FleetHub> hub,
    IServiceScopeFactory scopes)
{
    public async Task RobotUpdatedAsync(string robotCode, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var robot = await db.Robots.AsNoTracking()
            .Where(r => r.RobotCode == robotCode)
            .Select(r => new {
                r.RobotId, r.RobotCode, r.WarehouseId,
                r.ConnectionStatus, r.OperationalStatus,
                r.BatteryPercent, r.BatteryVoltage,
                r.CurrentCoordX, r.CurrentCoordY, r.CurrentHeading,
                r.LastHeartbeatTime, r.LastTelemetryTime,
                r.UpdatedAt, r.IsActive
            }).SingleOrDefaultAsync(ct);
        if (robot != null)
            await hub.Clients.All.SendAsync("RobotUpdated", robot, ct);
    }

    // Push a battery-only event to dashboard clients after persistence.
    public Task BatteryUpdatedAsync(
        RobotBatteryResponse battery,
        CancellationToken ct = default)
        => hub.Clients.All.SendAsync("RobotBatteryUpdated", battery, ct);

    // Lightweight live LiDAR update: no database writes or mission ACK dependency.
    public Task LidarUpdatedAsync(int robotId, string robotCode,
        string status, decimal? distanceCm, DateTime receivedAt,
        CancellationToken ct = default)
        => hub.Clients.All.SendAsync("RobotLidarUpdated",
            new { robotId, robotCode, status, distanceCm, receivedAt }, ct);

    public Task TelemetryUpdatedAsync(string robotCode,
        RobotTelemetryEvent telemetry, DateTime receivedAt,
        CancellationToken ct = default)
        => hub.Clients.All.SendAsync("RobotTelemetryUpdated",
            new { robotCode, receivedAt,
                  telemetry.BatteryPercent, telemetry.BatteryVoltage,
                  telemetry.PositionX, telemetry.PositionY, telemetry.Heading,
                  telemetry.LinearVelocity, telemetry.AngularVelocity,
                  telemetry.CpuTemperature, telemetry.WifiSignalDbm }, ct);

    public async Task TaskUpdatedAsync(long taskId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var task = await db.TransportTasks.AsNoTracking()
            .Where(t => t.TaskId == taskId)
            .Select(t => new {
                t.TaskId, t.TaskTrackingCode, t.WarehouseId,
                t.TaskStatus, t.DispatchMode, t.PriorityLevel,
                t.ScheduledTime, t.AssignedAt, t.StartedAt,
                t.CompletedAt, t.FailureReason
            }).SingleOrDefaultAsync(ct);
        if (task != null)
            await hub.Clients.All.SendAsync("TaskUpdated", task, ct);
    }

    public async Task MissionUpdatedAsync(long assignmentId,
        CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        var mission = await db.Missions.AsNoTracking()
            .Where(m => m.AssignmentId == assignmentId)
            .Select(m => new {
                m.MissionId, m.MissionCode, m.AssignmentId,
                m.Assignment.TaskId, m.Assignment.RobotId,
                m.Assignment.Robot.RobotCode,
                m.Assignment.AssignmentStatus,
                m.Assignment.Task.TaskStatus,
                m.MissionStatus, m.StartExecutionTime,
                m.EndExecutionTime, m.LastProgressAt,
                m.TotalDurationSeconds, m.FailureReason
            }).SingleOrDefaultAsync(ct);
        if (mission == null) return;
        await hub.Clients.All.SendAsync("MissionUpdated", mission, ct);
        await TaskUpdatedAsync(mission.TaskId, ct);
        await RobotUpdatedAsync(mission.RobotCode, ct);
    }
}
