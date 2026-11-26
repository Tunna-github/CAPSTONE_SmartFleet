using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using SmartFleetBE.Hubs;
using SmartFleetBE;
using SmartFleetBE.DTOs;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class InterventionTests
{
    private static (SmartFleetDbContext db, TaskInterventionService service, QrDemoRouteService routes) Fixture(bool assigned = true, bool home = false, bool navigation = false)
    {
        var options = new DbContextOptionsBuilder<SmartFleetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var db = new SmartFleetDbContext(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mqtt:EnableWorkers"] = "true", ["DemoRoute:Enabled"] = home.ToString(),
                ["Cancellation:HomeArUcoId"] = "0", ["Navigation:Enabled"] = navigation.ToString() }).Build();
        var env = new TestEnvironment();
        var nav = new NavigationCoordinator(env, config, NullLogger<NavigationCoordinator>.Instance);
        if (home)
        {
            var map = nav.GetMap();
            map.Cells[1][1] = new WarehouseMapCell { Type = "qr", ArucoId = 0, Label = "HOME", MarkerYawDeg = 0, MarkerSizeM = 0.04 };
            map.Cells[1][2] = new WarehouseMapCell { Type = "qr", ArucoId = 1, Label = "PICKUP", MarkerYawDeg = 0, MarkerSizeM = 0.04 };
            map.Links = [["HOME", "PICKUP"]];
            Assert.True(nav.ReplaceMap(map).Success);
        }
        var routes = new QrDemoRouteService(nav, env, config, NullLogger<QrDemoRouteService>.Instance);
        db.TransportTasks.Add(new TransportTask { TaskId = 1, TaskStatus = assigned ? "EXECUTING" : "QUEUED",
            WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2, DispatchMode = "AUTO", PayloadWeightKg = 1m });
        db.Warehouses.Add(new Warehouse { WarehouseId = 1, IsActive = true });
        db.Stations.Add(new Station { StationId = 1, IsActive = true, StationType = "PICKUP",
            Zone = new Zone { ZoneId = 1, WarehouseId = 1, IsActive = true } });
        db.Stations.Add(new Station { StationId = 2, ZoneId = 1, IsActive = true, StationType = "DELIVERY" });
        if (assigned)
        {
            var robot = new Robot { RobotId = 1, RobotCode = "R1", OperationalStatus = "BUSY", ConnectionStatus = "ONLINE",
                LastHeartbeatTime = DateTime.UtcNow, LastTelemetryTime = DateTime.UtcNow, WarehouseId = 1, IsActive = true,
                RobotType = "PHYSICAL", BatteryPercent = 100m, MaxPayloadWeightKg = 10m };
            var a = new TaskAssignment { AssignmentId = 1, TaskId = 1, Robot = robot, AssignmentStatus = "ACTIVE" };
            db.TaskAssignments.Add(a);
            db.Missions.Add(new Mission { MissionId = 1, Assignment = a, MissionStatus = "NAVIGATING_TO_PICKUP" });
        }
        db.SaveChanges();
        return (db, new TaskInterventionService(db, routes, config), routes);
    }

    [Theory]
    [InlineData("INITIALIZING", false, true, true, true)]
    [InlineData("NAVIGATING_TO_PICKUP", false, true, true, true)]
    [InlineData("AT_PICKUP", false, true, true, false)]
    [InlineData("NAVIGATING_TO_DELIVERY", false, true, true, false)]
    [InlineData("NAVIGATING_TO_PICKUP", true, true, true, false)]
    [InlineData("NAVIGATING_TO_PICKUP", false, false, true, false)]
    [InlineData("NAVIGATING_TO_PICKUP", false, true, false, false)]
    [InlineData("FAILED", false, true, true, false)]
    public void DirectCancelEligibility(string status, bool pickup, bool healthy, bool fresh, bool expected) =>
        Assert.Equal(expected, CancellationPolicy.CanDirectCancel(status, pickup, healthy, fresh));

    [Fact]
    public async Task QueuedCancellationIsPersistedWithHistory()
    {
        var (db, service, _) = Fixture(false);
        await service.Act(1, "cancel", new("Out of stock"), 42, default);
        Assert.Equal("CANCELLED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal(42, db.TaskStatusHistories.Single().ChangedBy);
        Assert.Equal(42, db.SystemAuditLogs.Single().UserId);
        Assert.Empty(db.RobotCommandOutbox);
    }

    [Fact]
    public async Task MovingCancellationDoesNotReleaseRobotBeforeHomeAck()
    {
        var (db, service, _) = Fixture();
        await service.Act(1, "cancel", new("Wrong SKU"), 42, default);
        Assert.Equal("CANCELLED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("BUSY", db.Robots.Single().OperationalStatus);
        Assert.Equal("CANCEL_STOP_PENDING", db.TaskInterventions.Single().State);
        Assert.Contains("CANCEL_STOP", db.RobotCommandOutbox.Single().Payload);
    }

    [Fact]
    public async Task PausePreservesTaskMissionAndAssignment()
    {
        var (db, service, _) = Fixture();
        await service.Act(1, "pause", new("Collision risk"), 42, default);
        Assert.Equal("EXECUTING", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("NAVIGATING_TO_PICKUP", db.Missions.Single().MissionStatus);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.True(await service.IsHeld(1, default));
    }

    [Theory]
    [InlineData("NAVIGATING_TO_DELIVERY", "ONLINE")]
    [InlineData("NAVIGATING_TO_PICKUP", "OFFLINE")]
    public async Task LoadOrOfflineCannotDirectCancel(string status, string connection)
    {
        var (db, service, _) = Fixture();
        db.Missions.Single().MissionStatus = status;
        db.Robots.Single().ConnectionStatus = connection;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "cancel", new("Business error"), 42, default));
        Assert.Equal("EXECUTING", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
    }

    [Fact]
    public async Task RequestThenForceRequiresBothPhysicalConfirmations()
    {
        var (db, service, _) = Fixture();
        await service.Act(1, "request-cancel", new("Load stuck"), 42, default);
        Assert.Equal("EXECUTING", db.TransportTasks.Single().TaskStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "force-cancel", new("Recovered", true, false), 43, default));
        // Rejected commands use a fresh scope in production; remove unsaved audit from this test scope.
        db.ChangeTracker.Clear();
        await service.Act(1, "force-cancel", new("Unloaded and isolated", true, true), 43, default);
        Assert.Equal("CANCELLED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("CANCELLED", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("ABORTED", db.Missions.Single().MissionStatus);
        Assert.Equal("MAINTENANCE", db.Robots.Single().OperationalStatus);
        var audit = db.SystemAuditLogs.Single(x => x.ActionName == "force-cancel");
        Assert.Equal(43, audit.UserId);
        Assert.Contains("BUSY", audit.Description); // snapshot before mutation
    }

    [Fact]
    public async Task InvalidAndDuplicateRobotAcknowledgementsCannotReleaseRobot()
    {
        var (db, service, _) = Fixture();
        await service.Act(1, "cancel", new("Wrong SKU"), 42, default);
        var control = db.TaskInterventions.Single();
        var e = new RobotMissionEvent { Event = "CONTROL_STOPPED", TaskId = 1, AssignmentId = 1, MissionId = 1,
            CommandId = "incorrect" };
        Assert.Equal(MissionEventResult.Rejected, await service.HandleEvent("R1", e, default));
        e.CommandId = control.CommandId;
        Assert.Equal(MissionEventResult.Rejected, await service.HandleEvent("OTHER_ROBOT", e, default));
        Assert.Equal(MissionEventResult.Applied, await service.HandleEvent("R1", e, default));
        Assert.Equal(MissionEventResult.Duplicate, await service.HandleEvent("R1", e, default));
        Assert.Equal("CANCELLED_HOLD", control.State); // no configured Home => physical recovery
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("BUSY", db.Robots.Single().OperationalStatus);
        await service.Act(1, "request-cancel", new("Recover unknown pose"), 42, default);
        Assert.Equal("CANCEL_REQUESTED", control.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeArrivalReleasesOnlyAfterCorrelatedCameraConfirmation(bool navigation)
    {
        var (db, service, _) = Fixture(home: true, navigation: navigation);
        db.Missions.Single().MissionStatus = "INITIALIZING";
        await db.SaveChangesAsync();
        await service.Act(1, "cancel", new("Wrong SKU"), 42, default);
        var c = db.TaskInterventions.Single();
        var stopped = new RobotMissionEvent { Event = "CONTROL_STOPPED", TaskId = 1,
            AssignmentId = 1, MissionId = 1, CommandId = c.CommandId, CurrentArUcoId = 1 };
        Assert.Equal(MissionEventResult.Applied, await service.HandleEvent("R1", stopped, default));
        Assert.Equal("RETURNING_HOME", c.State);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Contains("\"arucoId\"", db.RobotCommandOutbox.OrderBy(x => x.Id).Last().Payload);
        var home = new RobotMissionEvent { Event = "HOME_REACHED", TaskId = 1,
            AssignmentId = 1, MissionId = 1, CommandId = c.CommandId, CurrentArUcoId = 1 };
        Assert.Equal(MissionEventResult.Rejected, await service.HandleEvent("R1", home, default));
        home.CurrentArUcoId = 0;
        Assert.Equal(MissionEventResult.Applied, await service.HandleEvent("R1", home, default));
        Assert.Equal("CANCELLED", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("AVAILABLE", db.Robots.Single().OperationalStatus);
    }

    [Fact]
    public async Task MissionFailureKeepsPhysicalAssignmentForMaintenanceRecovery()
    {
        var (db, service, routes) = Fixture();
        var lifecycle = new MissionLifecycleService(db, new ConfigurationBuilder().Build(), routes, service);
        Assert.Equal(MissionEventResult.Applied, await lifecycle.HandleAsync("R1", new RobotMissionEvent {
            Event = "MISSION_FAILED", TaskId = 1, AssignmentId = 1, MissionId = 1, Reason = "Hardware fault" }));
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("ERROR", db.Robots.Single().OperationalStatus);
        await service.Act(1, "request-cancel", new("Recover load"), 42, default);
        await service.Act(1, "force-cancel", new("Load removed; power isolated", true, true), 43, default);
        Assert.Equal("MAINTENANCE", db.Robots.Single().OperationalStatus);
    }

    [Fact]
    public async Task LateMissionCompletionCannotClearPause()
    {
        var (db, service, routes) = Fixture();
        await service.Act(1, "pause", new("Collision risk"), 42, default);
        var lifecycle = new MissionLifecycleService(db, new ConfigurationBuilder().Build(), routes, service);
        Assert.Equal(MissionEventResult.Rejected, await lifecycle.HandleAsync("R1", new RobotMissionEvent {
            Event = "MISSION_COMPLETED", TaskId = 1, AssignmentId = 1, MissionId = 1 }));
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("BUSY", db.Robots.Single().OperationalStatus);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task RecoveryRejectsMissingPhysicalConfirmations(bool stopped, bool unloaded, bool returned)
    {
        var (db, service, _) = Fixture();
        db.TransportTasks.Single().TaskStatus = "FAILED";
        db.Robots.Single().OperationalStatus = "ERROR";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "recover-requeue",
            new("Recovered load", stopped, unloaded, returned, 1), 43, default));
        Assert.Equal("FAILED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Empty(db.RobotCommandOutbox);
    }

    [Theory]
    [InlineData("COMPLETED")]
    [InlineData("CANCELLED")]
    [InlineData("QUEUED")]
    public async Task RecoveryCannotResurrectTerminalOrUnfailedTasks(string status)
    {
        var (db, service, _) = Fixture();
        db.TransportTasks.Single().TaskStatus = status;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "recover-requeue",
            new("Recovered", true, true, true, 1), 43, default));
        Assert.Equal(status, db.TransportTasks.Single().TaskStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(999L)]
    public async Task RecoveryRejectsUnspecifiedOrStaleAssignment(long? expectedAssignment)
    {
        var (db, service, _) = Fixture();
        db.TransportTasks.Single().TaskStatus = "FAILED";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "recover-requeue",
            new("Goods returned", true, true, true, expectedAssignment), 43, default));
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
    }

    [Fact]
    public async Task RecoveryRejectsInactivePickup()
    {
        var (db, service, _) = Fixture();
        db.TransportTasks.Single().TaskStatus = "FAILED";
        db.Stations.Single(s => s.StationId == 1).IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.Act(1, "recover-requeue",
            new("Goods returned", true, true, true, 1), 43, default));
        Assert.Equal("FAILED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", db.TaskAssignments.Single().AssignmentStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveredTaskWaitsThirtySecondsThenAutoAssignsAnotherRobot(bool replacementAvailable)
    {
        var (db, _, routes) = Fixture();
        var task = db.TransportTasks.Single();
        task.TaskStatus = "FAILED";
        task.StartedAt = DateTime.UtcNow.AddMinutes(-5);
        task.AssignedAt = task.StartedAt;
        db.Missions.Single().MissionStatus = "FAILED";
        db.Missions.Single().FailureReason = "Original device fault";
        db.Robots.Single().OperationalStatus = "ERROR";
        db.Robots.Add(new Robot { RobotId = 2, RobotCode = "R2", WarehouseId = 1, RobotType = "PHYSICAL",
            OperationalStatus = "AVAILABLE", ConnectionStatus = "ONLINE", IsActive = replacementAvailable,
            BatteryPercent = 80m, MaxPayloadWeightKg = 10m,
            LastHeartbeatTime = DateTime.UtcNow, LastTelemetryTime = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var clock = new TestClock();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AutoDispatch:VerifiedPayloadLimitKg"] = "10" }).Build();
        var recovery = new TaskInterventionService(db, routes, config, clock);
        var now = clock.GetUtcNow().UtcDateTime;
        await recovery.Act(1, "recover-requeue", new("Goods back at original pickup", true, true, true, 1), 43, default);
        Assert.Equal("QUEUED", task.TaskStatus);
        Assert.Equal(now.AddSeconds(30), task.ScheduledTime);
        Assert.Null(task.StartedAt);
        Assert.Null(task.AssignedAt);
        Assert.Equal("FAILED", db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("FAILED", db.Missions.Single().MissionStatus);
        Assert.Equal("Original device fault", db.Missions.Single().FailureReason);
        Assert.Equal("MAINTENANCE", db.Robots.Single(r => r.RobotId == 1).OperationalStatus);
        Assert.False(await recovery.IsHeld(1, default));
        Assert.Contains("\"LoadReturnedToPickupConfirmed\":true", db.SystemAuditLogs.Single().Description);
        var lifecycle = new MissionLifecycleService(db, config, routes, recovery);
        Assert.Equal(MissionEventResult.Rejected, await lifecycle.HandleAsync("R1", new RobotMissionEvent {
            Event = "MISSION_STARTED", TaskId = 1, AssignmentId = 1, MissionId = 1 }));

        // Restore old robot deliberately: it still must not win the retry with its higher battery.
        db.Robots.Single(r => r.RobotId == 1).OperationalStatus = "AVAILABLE";
        await db.SaveChangesAsync();
        var options = (DbContextOptions<SmartFleetDbContext>)db.GetService<IDbContextOptions>();
        using var provider = new ServiceCollection().AddScoped(_ => new SmartFleetDbContext(options)).BuildServiceProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        var dispatcher = new AutoDispatchService(factory, config,
            new FleetRealtimePublisher(new SinkHub(), factory), NullLogger<AutoDispatchService>.Instance, clock);
        Assert.False(await dispatcher.TryAssignAsync(1));
        clock.Advance(29);
        Assert.False(await dispatcher.TryAssignAsync(1));
        clock.Advance(1);
        if (!replacementAvailable)
        {
            Assert.False(await dispatcher.TryAssignAsync(1));
            db.ChangeTracker.Clear();
            Assert.Equal("QUEUED", db.TransportTasks.Single().TaskStatus);
            Assert.Empty(db.TaskAssignments.Where(a => a.AssignmentStatus == "ACTIVE"));
            // An eligible robot arriving later is picked up on a subsequent polling attempt.
            db.Robots.Single(r => r.RobotId == 2).IsActive = true;
            await db.SaveChangesAsync();
            clock.Advance(5);
        }
        Assert.True(await dispatcher.TryAssignAsync(1));
        Assert.False(await dispatcher.TryAssignAsync(1)); // worker retry cannot create a duplicate assignment
        db.ChangeTracker.Clear();
        Assert.Equal("ASSIGNED", db.TransportTasks.Single().TaskStatus);
        Assert.Equal(2, db.TaskAssignments.Single(a => a.AssignmentStatus == "ACTIVE").RobotId);
        Assert.Equal(2, db.Missions.Count());
        Assert.Equal(2, db.RobotCommandOutbox.Count()); // old robot stop + new robot execution
    }

    [Fact]
    public async Task RecoveryPreservesManualModeAndBlocksEarlyManualAssignment()
    {
        var (db, service, _) = Fixture();
        var task = db.TransportTasks.Single();
        task.DispatchMode = "MANUAL";
        task.TaskStatus = "FAILED";
        await db.SaveChangesAsync();
        await service.Act(1, "recover-requeue", new("Back at pickup", true, true, true, 1), 43, default);
        Assert.Equal("MANUAL", task.DispatchMode);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AutoDispatch:VerifiedPayloadLimitKg"] = "10" }).Build();
        var manual = new TaskDispatchService(db, config, NullLogger<TaskDispatchService>.Instance);
        var result = await manual.AssignManualAsync(1, 1, 42);
        Assert.Contains("waiting", result.Error!);
        Assert.Empty(db.TaskAssignments.Where(a => a.AssignmentStatus == "ACTIVE"));
        task.ScheduledTime = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        var oldRobot = await manual.AssignManualAsync(1, 1, 42);
        Assert.Contains("previously failed", oldRobot.Error!);
        var options = (DbContextOptions<SmartFleetDbContext>)db.GetService<IDbContextOptions>();
        using var provider = new ServiceCollection().AddScoped(_ => new SmartFleetDbContext(options)).BuildServiceProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        var auto = new AutoDispatchService(factory, config, new FleetRealtimePublisher(new SinkHub(), factory),
            NullLogger<AutoDispatchService>.Instance);
        Assert.False(await auto.TryAssignAsync(1)); // manual tasks never become AUTO after recovery
    }

    [Fact]
    public async Task RobotListExplainsExcludedRobotsAndNeverOffersFailedRobotAgain()
    {
        var (db, _, _) = Fixture(false);
        for (int i = 1; i <= 6; i++) db.Robots.Add(new Robot { RobotId = i, RobotCode = $"R0{i}",
            WarehouseId = 1, IsActive = true, RobotType = "PHYSICAL", MaxPayloadWeightKg = 10,
            BatteryPercent = i == 3 ? 15 : 80, ConnectionStatus = i == 4 ? "OFFLINE" : "ONLINE",
            OperationalStatus = i == 5 ? "MAINTENANCE" : "AVAILABLE",
            LastHeartbeatTime = DateTime.UtcNow.AddSeconds(i == 6 ? -120 : 0),
            LastTelemetryTime = DateTime.UtcNow.AddSeconds(i == 6 ? -120 : 0) });
        await db.SaveChangesAsync();
        var config = DispatchConfig();
        var service = new TaskDispatchService(db, config, NullLogger<TaskDispatchService>.Instance);
        var eligible = await service.GetAvailableRobotsAsync(1);
        Assert.Equal(new[] { 1, 2 }, eligible.Value!.Select(r => r.RobotId));
        var candidates = await service.GetAvailableRobotsAsync(1, includeIneligible: true);
        Assert.Equal(6, candidates.Value!.Count);
        Assert.All(candidates.Value!.Where(r => r.RobotId >= 3), r => {
            Assert.False(r.IsEligible); Assert.False(string.IsNullOrWhiteSpace(r.IneligibilityReason)); });
        db.TaskAssignments.Add(new TaskAssignment { TaskId = 1, RobotId = 1, AssignmentStatus = "FAILED" });
        await db.SaveChangesAsync();
        eligible = await service.GetAvailableRobotsAsync(1);
        Assert.Equal(2, Assert.Single(eligible.Value!).RobotId);
    }

    [Theory]
    [InlineData("STALE")]
    [InlineData("MISSING_TELEMETRY")]
    [InlineData("OFFLINE")]
    [InlineData("BUSY")]
    [InlineData("LOW_BATTERY")]
    [InlineData("REMOVED")]
    public async Task RobotChangingAfterSelectionCannotBeManuallyOrAutomaticallyAssigned(string change)
    {
        var (db, _, _) = Fixture(false);
        var r = new Robot { RobotId = 1, RobotCode = "R01", WarehouseId = 1, IsActive = true,
            RobotType = "PHYSICAL", MaxPayloadWeightKg = 10, BatteryPercent = 80,
            ConnectionStatus = "ONLINE", OperationalStatus = "AVAILABLE",
            LastHeartbeatTime = DateTime.UtcNow, LastTelemetryTime = DateTime.UtcNow };
        db.Robots.Add(r);
        await db.SaveChangesAsync();
        var config = DispatchConfig();
        var manual = new TaskDispatchService(db, config, NullLogger<TaskDispatchService>.Instance);
        Assert.Single((await manual.GetAvailableRobotsAsync(1)).Value!);
        switch (change)
        {
            case "STALE": r.LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-5); break;
            case "MISSING_TELEMETRY": r.LastTelemetryTime = null; break;
            case "OFFLINE": r.ConnectionStatus = "OFFLINE"; break;
            case "BUSY": r.OperationalStatus = "BUSY"; break;
            case "LOW_BATTERY": r.BatteryPercent = 5; break;
            case "REMOVED": db.Robots.Remove(r); break;
        }
        await db.SaveChangesAsync();
        var result = await manual.AssignManualAsync(1, 1, 42);
        Assert.NotEqual(SmartFleetBE.Services.Results.TransportTaskResultStatus.Success, result.Status);
        Assert.Empty((await manual.GetAvailableRobotsAsync(1)).Value!);
        var options = (DbContextOptions<SmartFleetDbContext>)db.GetService<IDbContextOptions>();
        using var provider = new ServiceCollection().AddScoped(_ => new SmartFleetDbContext(options)).BuildServiceProvider();
        var factory = provider.GetRequiredService<IServiceScopeFactory>();
        var auto = new AutoDispatchService(factory, config, new FleetRealtimePublisher(new SinkHub(), factory),
            NullLogger<AutoDispatchService>.Instance);
        Assert.False(await auto.TryAssignAsync(1));
        Assert.Empty(db.TaskAssignments);
        Assert.Empty(db.Missions);
        Assert.Empty(db.RobotCommandOutbox);
        Assert.Equal("QUEUED", db.TransportTasks.Single().TaskStatus);
    }

    [Theory]
    [InlineData("READY", true)]
    [InlineData("OLD_CYCLE", false)]
    [InlineData("RETURNING", false)]
    [InlineData("NO_STOP", false)]
    [InlineData("NO_BERTH", false)]
    [InlineData("OPEN_FAILURE", false)]
    [InlineData("NEW_FAILURE", false)]
    [InlineData("OFFLINE", false)]
    public async Task ManualRetryRequiresSafeDockingAfterEveryFailedAttempt(string scenario, bool allowed)
    {
        var (db, _, _) = Fixture();
        var now = DateTime.UtcNow;
        var task = db.TransportTasks.Single(); task.TaskStatus = "QUEUED"; task.DispatchMode = "AUTO";
        var old = db.TaskAssignments.Single(); old.AssignmentStatus = "FAILED";
        old.EndedAt = scenario == "OPEN_FAILURE" ? null : now.AddMinutes(-2);
        db.Missions.Single().MissionStatus = "FAILED";
        var robot = db.Robots.Single(); robot.OperationalStatus = "AVAILABLE";
        if (scenario == "OFFLINE") robot.ConnectionStatus = "OFFLINE";
        var job = new ChargingJob { Id = Guid.NewGuid(), RobotId = 1, SourceMissionId = 1, StationId = 3,
            State = scenario == "RETURNING" ? "RETURNING" : "READY",
            CreatedAt = scenario == "OLD_CYCLE" ? now.AddMinutes(-3) : now.AddMinutes(-1),
            LastStoppedAt = scenario == "NO_STOP" ? null : now, UpdatedAt = now };
        db.ChargingJobs.Add(job);
        if (scenario != "NO_BERTH") db.StationBerths.Add(new() { StationId = 3, RobotId = 1, ReservedAt = now });
        if (scenario == "NEW_FAILURE") db.TaskAssignments.Add(new() { TaskId = 1, RobotId = 1,
            AssignmentStatus = "FAILED", EndedAt = now.AddSeconds(-10) });
        await db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AutoDispatch:VerifiedPayloadLimitKg"] = "10", ["Charging:Enabled"] = "true" }).Build();
        var service = new TaskDispatchService(db, config, NullLogger<TaskDispatchService>.Instance);
        var candidates = await service.GetAvailableRobotsAsync(1, includeIneligible: true);
        Assert.Equal(allowed, Assert.Single(candidates.Value!).IsEligible);

        if (allowed) {
            var options = (DbContextOptions<SmartFleetDbContext>)db.GetService<IDbContextOptions>();
            using var provider = new ServiceCollection().AddScoped(_ => new SmartFleetDbContext(options)).BuildServiceProvider();
            var factory = provider.GetRequiredService<IServiceScopeFactory>();
            var auto = new AutoDispatchService(factory, config, new FleetRealtimePublisher(new SinkHub(), factory),
                NullLogger<AutoDispatchService>.Instance);
            Assert.False(await auto.TryAssignAsync(1)); // even recovered/docked robot stays excluded from AUTO
        }
        var result = await service.AssignManualAsync(1, 1, 42);
        Assert.Equal(allowed, result.Status == SmartFleetBE.Services.Results.TransportTaskResultStatus.Success);
        Assert.Equal("FAILED", old.AssignmentStatus); // retain original evidence
        Assert.Equal("FAILED", db.Missions.Single(m => m.MissionId == 1).MissionStatus);
        Assert.Equal(allowed ? 1 : 0, db.TaskAssignments.Count(a => a.AssignmentStatus == "ACTIVE"));
        Assert.Equal(allowed ? "DEPARTING" : job.State, db.ChargingJobs.Single().State);
    }

    private static IConfiguration DispatchConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["AutoDispatch:VerifiedPayloadLimitKg"] = "10" }).Build();

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class SinkHub : IHubContext<FleetHub>
    {
        public IHubClients Clients { get; } = new SinkClients();
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private sealed class SinkClients : IHubClients
    {
        private static readonly IClientProxy Sink = new SinkProxy();
        public IClientProxy All => Sink;
        public IClientProxy AllExcept(IReadOnlyList<string> x) => Sink;
        public IClientProxy Client(string x) => Sink;
        public IClientProxy Clients(IReadOnlyList<string> x) => Sink;
        public IClientProxy Group(string x) => Sink;
        public IClientProxy GroupExcept(string x, IReadOnlyList<string> y) => Sink;
        public IClientProxy Groups(IReadOnlyList<string> x) => Sink;
        public IClientProxy User(string x) => Sink;
        public IClientProxy Users(IReadOnlyList<string> x) => Sink;
    }
    private sealed class SinkProxy : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "smartfleet-tests-" + Guid.NewGuid());
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
