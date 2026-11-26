using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Controllers;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Repositories;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class CancelledTaskTests
{
    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "cancelled-task-" + Guid.NewGuid());
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Fixture : IDisposable
    {
        public readonly SmartFleetDbContext Db;
        public readonly CancelledTaskService Service;
        public readonly TransportTaskRepository Repository;
        private readonly Env env = new();
        public Fixture(bool withMission = true)
        {
            Db = new(new DbContextOptionsBuilder<SmartFleetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            var config = new ConfigurationBuilder().Build();
            var nav = new NavigationCoordinator(env, config, NullLogger<NavigationCoordinator>.Instance);
            var routes = new QrDemoRouteService(nav, env, config, NullLogger<QrDemoRouteService>.Instance);
            Service = new(Db, routes, config); Repository = new(Db);
            Db.Warehouses.Add(new() { WarehouseId = 1, IsActive = true });
            Db.Zones.Add(new() { ZoneId = 1, WarehouseId = 1, IsActive = true });
            Db.Stations.AddRange(new Station { StationId = 1, ZoneId = 1, StationType = "TRANSFER", IsActive = true },
                new Station { StationId = 2, ZoneId = 1, StationType = "TRANSFER", IsActive = true });
            Db.TransportTasks.Add(new() { TaskId = 1, WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2,
                TaskStatus = "CANCELLED", DispatchMode = "MANUAL", CancelledAt = DateTime.UtcNow.AddMinutes(-1), CancellationReason = "Old cancellation" });
            Db.TaskInterventions.Add(new() { TaskId = 1, State = "CANCELLED_HOME", CommandId = withMission ? "actual-stop" : null!, CommandAcknowledged = withMission });
            Db.TaskStatusHistories.Add(new() { TaskId = 1, PreviousStatus = "QUEUED", NewStatus = "CANCELLED", Reason = "Old cancellation" });
            if (withMission) {
                Db.Robots.Add(new() { RobotId = 1, OperationalStatus = "CHARGING" });
                Db.TaskAssignments.Add(new() { AssignmentId = 1, TaskId = 1, RobotId = 1, AssignmentStatus = "CANCELLED" });
                Db.Missions.Add(new() { MissionId = 1, AssignmentId = 1, MissionStatus = "ABORTED" });
                Db.DeliveryVerifications.Add(new() { MissionId = 1, VerificationStage = "PICKUP", IsVerified = true });
            }
            Db.SaveChanges();
        }
        public void Dispose() { Db.Dispose(); if (Directory.Exists(env.ContentRootPath)) Directory.Delete(env.ContentRootPath, true); }
    }

    [Theory]
    [InlineData("MANUAL", false)]
    [InlineData("AUTO", false)]
    [InlineData("AUTO", true)]
    public async Task RequeuePreservesModeScheduleAndEvidenceWithoutReleasingRobot(string mode, bool futureSchedule)
    {
        using var f = new Fixture(); var task = f.Db.TransportTasks.Single(); task.DispatchMode = mode;
        var schedule = DateTime.UtcNow.AddHours(2); if (futureSchedule) task.ScheduledTime = schedule;
        await f.Db.SaveChangesAsync(); var start = DateTime.UtcNow;
        await f.Service.Requeue(1, new("Goods returned to pickup", true), 42, default);
        Assert.Equal("QUEUED", task.TaskStatus); Assert.Equal(mode, task.DispatchMode);
        Assert.True(task.ScheduledTime >= start.AddSeconds(30));
        if (futureSchedule) Assert.Equal(schedule, task.ScheduledTime);
        Assert.Null(task.CancelledAt); Assert.Null(task.CancellationReason);
        Assert.Equal("NORMAL", f.Db.TaskInterventions.Single().State);
        Assert.Equal("CHARGING", f.Db.Robots.Single().OperationalStatus);
        Assert.Equal("CANCELLED", f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("ABORTED", f.Db.Missions.Single().MissionStatus);
        Assert.Single(f.Db.DeliveryVerifications); Assert.Equal(2, f.Db.TaskStatusHistories.Count());
        Assert.Equal(42, f.Db.SystemAuditLogs.Single().UserId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Requeue(1, new("Duplicate", true), 42, default));
        Assert.Single(f.Db.SystemAuditLogs);
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("MISSION_RUNNING")]
    [InlineData("CANCEL_STOP_PENDING")]
    [InlineData("RETURNING_HOME")]
    [InlineData("UNACKNOWLEDGED")]
    [InlineData("UNKNOWN_CONTROL")]
    public async Task BothActionsRejectUnfinishedCancellation(string state)
    {
        using var f = new Fixture();
        if (state == "ACTIVE") f.Db.TaskAssignments.Single().AssignmentStatus = "ACTIVE";
        else if (state == "MISSION_RUNNING") f.Db.Missions.Single().MissionStatus = "NAVIGATING_TO_PICKUP";
        else if (state == "UNACKNOWLEDGED") f.Db.TaskInterventions.Single().CommandAcknowledged = false;
        else if (state == "UNKNOWN_CONTROL") f.Db.TaskInterventions.Remove(f.Db.TaskInterventions.Single());
        else f.Db.TaskInterventions.Single().State = state;
        await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Requeue(1, new("Retry", true), 42, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Delete(1, 42, default));
        Assert.Equal("CANCELLED", f.Db.TransportTasks.Single().TaskStatus); Assert.Null(f.Db.TransportTasks.Single().DeletedAt);
        Assert.Empty(f.Db.SystemAuditLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteHidesOnlyTheCancelledTaskAndRetainsHistory(bool withMission)
    {
        using var f = new Fixture(withMission);
        await f.Service.Delete(1, 42, default);
        Assert.Empty(await f.Repository.GetAllAsync()); Assert.Null(await f.Repository.GetByIdAsync(1));
        Assert.NotNull(f.Db.TransportTasks.Single().DeletedAt); Assert.Equal(42, f.Db.TransportTasks.Single().DeletedBy);
        Assert.Equal("CANCELLED", f.Db.TransportTasks.Single().TaskStatus);
        Assert.Single(f.Db.TaskStatusHistories); Assert.Equal(withMission ? 1 : 0, f.Db.Missions.Count());
        Assert.Equal("DELETE_CANCELLED_TASK", f.Db.SystemAuditLogs.Single().ActionName);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.Delete(1, 42, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Service.Requeue(1, new("Retry", true), 42, default));
    }

    [Fact] public async Task RequeueNeedsInventoryConfirmationAndValidOriginalStations()
    {
        using var f = new Fixture(false);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Requeue(1, new("Retry", false), 42, default));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.Requeue(1, new(" ", true), 42, default));
        f.Db.Stations.Single(s => s.StationId == 1).IsActive = false; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Requeue(1, new("Retry", true), 42, default));
        Assert.Empty(f.Db.SystemAuditLogs); Assert.Equal("CANCELLED", f.Db.TransportTasks.Single().TaskStatus);
        await f.Service.Delete(1, 42, default); // obsolete station does not prevent operational removal
    }

    [Theory]
    [InlineData("QUEUED")]
    [InlineData("COMPLETED")]
    [InlineData("FAILED")]
    public async Task CancelledActionsCannotChangeOtherTaskStates(string state)
    {
        using var f = new Fixture(false); f.Db.TransportTasks.Single().TaskStatus = state; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Delete(1, 42, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.Requeue(1, new("Retry", true), 42, default));
    }

    [Fact] public void BothActionsRequireWarehouseOperator()
    {
        var roles = typeof(CancelledTasksController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>();
        Assert.Equal("Warehouse Operator", Assert.Single(roles).Roles);
    }
}
