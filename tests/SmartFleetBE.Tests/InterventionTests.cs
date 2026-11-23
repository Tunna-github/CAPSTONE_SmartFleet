using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE;
using SmartFleetBE.DTOs;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class InterventionTests
{
    private static (SmartFleetDbContext db, TaskInterventionService service, QrDemoRouteService routes) Fixture(bool assigned = true, bool home = false)
    {
        var options = new DbContextOptionsBuilder<SmartFleetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false))
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var db = new SmartFleetDbContext(options);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mqtt:EnableWorkers"] = "true", ["DemoRoute:Enabled"] = home.ToString(),
                ["Cancellation:HomeArUcoId"] = "0" }).Build();
        var env = new TestEnvironment();
        var nav = new NavigationCoordinator(env, config, NullLogger<NavigationCoordinator>.Instance);
        if (home)
        {
            var map = nav.GetMap();
            map.Cells[1][1] = new WarehouseMapCell { Type = "qr", ArucoId = 0, Label = "HOME" };
            map.Cells[1][2] = new WarehouseMapCell { Type = "qr", ArucoId = 1, Label = "PICKUP" };
            map.Links = [["HOME", "PICKUP"]];
            Assert.True(nav.ReplaceMap(map).Success);
        }
        var routes = new QrDemoRouteService(nav, env, config, NullLogger<QrDemoRouteService>.Instance);
        db.TransportTasks.Add(new TransportTask { TaskId = 1, TaskStatus = assigned ? "EXECUTING" : "QUEUED" });
        if (assigned)
        {
            var robot = new Robot { RobotId = 1, RobotCode = "R1", OperationalStatus = "BUSY", ConnectionStatus = "ONLINE",
                LastTelemetryTime = DateTime.UtcNow };
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

    [Fact]
    public async Task HomeArrivalReleasesOnlyAfterCorrelatedCameraConfirmation()
    {
        var (db, service, _) = Fixture(home: true);
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
