using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Constants;
using SmartFleetBE.Controllers;
using SmartFleetBE.DTOs;
using SmartFleetBE.DTOs.TransportTasks;
using SmartFleetBE.Hubs;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Repositories;
using SmartFleetBE.Services;
using SmartFleetBE.Services.Results;

namespace SmartFleetBE.Tests;

public sealed class BusinessRegressionTests
{
    [Fact]
    public void RevokingAnAccountDisconnectsOnlyItsCurrentFleetConnections()
    {
        var registry = new FleetConnectionRegistry();
        var disconnected = new List<string>();
        registry.Add("operator-a", 1, () => disconnected.Add("operator-a"));
        registry.Add("operator-b", 1, () => disconnected.Add("operator-b"));
        registry.Add("maintenance", 2, () => disconnected.Add("maintenance"));
        registry.Add("closed-tab", 1, () => disconnected.Add("closed-tab"));
        registry.Remove("closed-tab");

        registry.RevokeUser(1);
        registry.RevokeUser(1);

        Assert.Equal(new[] { "operator-a", "operator-b" }, disconnected.OrderBy(x => x));
        registry.RevokeUser(2);
        Assert.Contains("maintenance", disconnected);
    }

    private sealed class Fixture : IDisposable
    {
        public ServiceProvider Provider { get; }
        public IServiceScope Scope { get; }
        public SmartFleetDbContext Db { get; }
        public IConfiguration Config { get; }
        public QrDemoRouteService Routes { get; }
        public TaskInterventionService Interventions { get; }
        public NavigationCoordinator Navigation { get; }
        private readonly string root = Path.Combine(Path.GetTempPath(), "smartfleet-audit-tests-" + Guid.NewGuid());
        public Fixture()
        {
            var name = Guid.NewGuid().ToString();
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Mqtt:EnableWorkers"] = "true", ["AutoDispatch:VerifiedPayloadLimitKg"] = "10" }).Build();
            var services = new ServiceCollection().AddLogging().AddSingleton(Config);
            services.AddDbContext<SmartFleetDbContext>(o => o.UseInMemoryDatabase(name, b => b.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<IHubContext<FleetHub>>(new Hub());
            services.AddSingleton<FleetRealtimePublisher>();
            Provider = services.BuildServiceProvider(); Scope = Provider.CreateScope();
            Db = Scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
            var env = new Env { ContentRootPath = root };
            var nav = new NavigationCoordinator(env, Config, NullLogger<NavigationCoordinator>.Instance);
            Navigation = nav;
            Routes = new QrDemoRouteService(nav, env, Config, NullLogger<QrDemoRouteService>.Instance);
            Interventions = new TaskInterventionService(Db, Routes, Config);
            Db.Warehouses.Add(new Warehouse { WarehouseId = 1, IsActive = true });
            Db.Stations.AddRange(new Station { StationId = 1, IsActive = true, StationType = "PICKUP", Zone = new Zone { ZoneId = 1, WarehouseId = 1, IsActive = true } },
                new Station { StationId = 2, ZoneId = 1, IsActive = true, StationType = "DELIVERY" });
            Db.TransportTasks.Add(new TransportTask { TaskId = 1, TaskStatus = "QUEUED", DispatchMode = "MANUAL",
                TaskTrackingCode = "TEST", WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2, PriorityLevel = 2, PayloadWeightKg = 1 });
            Db.Robots.Add(new Robot { RobotId = 1, RobotCode = "R01", WarehouseId = 1, IsActive = true,
                RobotType = "PHYSICAL", BatteryPercent = 80, MaxPayloadWeightKg = 10,
                ConnectionStatus = "ONLINE", OperationalStatus = "AVAILABLE",
                LastHeartbeatTime = DateTime.UtcNow, LastTelemetryTime = DateTime.UtcNow });
            Db.SaveChanges();
        }
        public void Assign()
        {
            Db.TransportTasks.Single().TaskStatus = "EXECUTING"; Db.Robots.Single().OperationalStatus = "BUSY";
            Db.TaskAssignments.Add(new TaskAssignment { AssignmentId = 1, TaskId = 1, RobotId = 1, AssignmentStatus = "ACTIVE" });
            Db.Missions.Add(new Mission { MissionId = 1, AssignmentId = 1, MissionStatus = "NAVIGATING_TO_PICKUP" });
            Db.RobotCommandOutbox.Add(new RobotCommandOutbox { AssignmentId = 1, Topic = "test", Payload = "{\"command\":\"EXECUTE_TASK\"}", DeliveredAt = DateTime.UtcNow });
            Db.SaveChanges();
        }
        public TransportTaskService Tasks() => new(new TransportTaskRepository(Db),
            new AutoDispatchService(Provider.GetRequiredService<IServiceScopeFactory>(), Config,
                Provider.GetRequiredService<FleetRealtimePublisher>(), NullLogger<AutoDispatchService>.Instance),
            Config, NullLogger<TransportTaskService>.Instance, Routes);
        public void Dispose() { Scope.Dispose(); Provider.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true,"SIMULATION",true)]
    [InlineData(true,"PHYSICAL",false)]
    [InlineData(false,"PHYSICAL",true)]
    [InlineData(false,"SIMULATION",false)]
    [InlineData(null,"PHYSICAL",true)]
    [InlineData(null,"SIMULATION",false)]
    public async Task ManualConfirmationAgreesWithListedRobotType(bool? simulation,string robotType,bool eligible)
    {
        using var f = new Fixture();
        f.Config["Simulation:Enabled"]=simulation?.ToString();
        f.Db.Robots.Single().RobotType=robotType;
        await f.Db.SaveChangesAsync();
        var manual=new TaskDispatchService(f.Db,f.Config,NullLogger<TaskDispatchService>.Instance);
        var listed=await manual.GetAvailableRobotsAsync(1,includeIneligible:true);
        Assert.Equal(eligible,Assert.Single(listed.Value!).IsEligible);
        var result=await manual.AssignManualAsync(1,1,42);
        Assert.Equal(eligible ? TransportTaskResultStatus.Success : TransportTaskResultStatus.Conflict,result.Status);
        Assert.Equal(eligible ? 1 : 0,f.Db.TaskAssignments.Count());
        Assert.Equal(eligible ? 1 : 0,f.Db.RobotCommandOutbox.Count());
        Assert.Equal(eligible ? "BUSY" : "AVAILABLE",f.Db.Robots.Single().OperationalStatus);
    }

    [Theory]
    [InlineData(true,"SIMULATION",true)]
    [InlineData(true,"PHYSICAL",false)]
    [InlineData(false,"PHYSICAL",true)]
    [InlineData(false,"SIMULATION",false)]
    public async Task AutoDispatchEnforcesTheSameRobotType(bool simulation,string robotType,bool eligible)
    {
        using var f=new Fixture();
        f.Config["Simulation:Enabled"]=simulation.ToString();
        f.Db.Robots.Single().RobotType=robotType;f.Db.TransportTasks.Single().DispatchMode="AUTO";
        await f.Db.SaveChangesAsync();
        var auto=new AutoDispatchService(f.Provider.GetRequiredService<IServiceScopeFactory>(),f.Config,
            f.Provider.GetRequiredService<FleetRealtimePublisher>(),NullLogger<AutoDispatchService>.Instance);
        Assert.Equal(eligible,await auto.TryAssignAsync(1));
        Assert.Equal(eligible ? 1 : 0,f.Db.TaskAssignments.Count());
    }

    [Theory]
    [InlineData("ERROR")]
    [InlineData("FATAL")]
    public async Task DatabaseIncidentSeverityBlocksBothDispatchModes(string severity)
    {
        using var f = new Fixture();
        f.Db.RobotIncidents.Add(new RobotIncident { RobotId = 1, Severity = severity }); await f.Db.SaveChangesAsync();
        var manual = new TaskDispatchService(f.Db, f.Config, NullLogger<TaskDispatchService>.Instance);
        Assert.Empty((await manual.GetAvailableRobotsAsync(1)).Value!);
        Assert.Equal(TransportTaskResultStatus.Conflict, (await manual.AssignManualAsync(1, 1, 1)).Status);
        f.Db.TransportTasks.Single().DispatchMode = "AUTO"; await f.Db.SaveChangesAsync();
        var auto = new AutoDispatchService(f.Provider.GetRequiredService<IServiceScopeFactory>(), f.Config,
            f.Provider.GetRequiredService<FleetRealtimePublisher>(), NullLogger<AutoDispatchService>.Instance);
        Assert.False(await auto.TryAssignAsync(1)); Assert.Empty(f.Db.TaskAssignments);
    }

    [Fact]
    public async Task QueuedTaskCanBeEditedButDispatchedTaskCannot()
    {
        using var f = new Fixture();
        var request = new UpdateTransportTaskRequest { WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2,
            PayloadWeightKg = 2, PriorityLevel = 3, PackageCode = "SKU-1" };
        Assert.Equal(TransportTaskResultStatus.Success, (await f.Tasks().UpdateAsync(1, request)).Status);
        Assert.Equal(3, f.Db.TransportTasks.Single().PriorityLevel);
        f.Assign();
        Assert.Equal(TransportTaskResultStatus.Conflict, (await f.Tasks().UpdateAsync(1, request)).Status);
        f.Db.TransportTasks.Single().TaskStatus = "QUEUED"; f.Db.TaskAssignments.Single().AssignmentStatus = "FAILED";
        await f.Db.SaveChangesAsync();
        Assert.Equal(TransportTaskResultStatus.Conflict, (await f.Tasks().UpdateAsync(1, request)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task EditCannotEraseValidPayloadWeight(int? weight)
    {
        using var f = new Fixture();
        var result = await f.Tasks().UpdateAsync(1, new() { WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2, PayloadWeightKg = weight });
        Assert.Equal(TransportTaskResultStatus.ValidationFailed, result.Status);
        Assert.Equal(1, f.Db.TransportTasks.Single().PayloadWeightKg);
    }

    [Fact]
    public async Task DeactivationAndRoleRevocationInvalidateExistingClaims()
    {
        using var f = new Fixture();
        var role = new Role { RoleId = 1, RoleName = AppRoles.WarehouseOperator };
        var user = new User { UserId = 42, IsActive = true, UserRoles = [new UserRole { RoleId = 1, Role = role }] };
        f.Db.Users.Add(user); await f.Db.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "42"), new(ClaimTypes.Role, AppRoles.WarehouseOperator)], "test"));
        Assert.True(await CurrentAccountValidator.IsCurrentAsync(principal, f.Db));
        user.IsActive = false; await f.Db.SaveChangesAsync();
        Assert.False(await CurrentAccountValidator.IsCurrentAsync(principal, f.Db));
        user.IsActive = true; role.RoleName = AppRoles.Admin; await f.Db.SaveChangesAsync();
        Assert.False(await CurrentAccountValidator.IsCurrentAsync(principal, f.Db));
    }

    [Fact]
    public async Task UserEditSavesRolesAndDoesNotDuplicateRetainedRoleKeys()
    {
        using var f = new Fixture();
        var op = new Role { RoleId = 1, RoleName = AppRoles.WarehouseOperator };
        var maintenance = new Role { RoleId = 2, RoleName = AppRoles.MaintenanceTechnician };
        f.Db.Roles.AddRange(op, maintenance);
        f.Db.Users.Add(new User { UserId = 42, Username = "tester", Email = "tester@example.test", FullName = "Before",
            IsActive = true, UserRoles = [new UserRole { RoleId = 1, Role = op }] }); await f.Db.SaveChangesAsync();
        var service = new UserService(new UserRepository(f.Db));
        var request = new SmartFleetBE.DTOs.Users.UpdateUserRequest { Username = "tester", Email = "tester@example.test",
            FullName = "After", Roles = [AppRoles.WarehouseOperator, AppRoles.MaintenanceTechnician] };
        var result = await service.UpdateAsync(42, request);
        Assert.Equal(2, result!.Roles.Count); Assert.Equal("After", result.FullName);
        await service.UpdateAsync(42, request); Assert.Equal(2, f.Db.UserRoles.Count());
        request.FullName = "Must not save"; request.Roles = ["Unsupported role"];
        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(42, request));
        Assert.Equal("After", f.Db.Users.Single().FullName);
    }

    [Fact]
    public void EmptyTelemetryCannotMakeRobotOnline()
    {
        Assert.False(MqttRobotTelemetryWorker.Valid(new()));
        Assert.False(MqttRobotTelemetryWorker.Valid(new() { BatteryPercent = 101 }));
        Assert.True(MqttRobotTelemetryWorker.Valid(new() { BatteryPercent = 0 }));
    }

    [Fact]
    public async Task TaskChangedDuringEditReturnsConflict()
    {
        using var f = new Fixture();
        using (var otherScope = f.Provider.CreateScope())
        {
            var other = otherScope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
            var task = await other.TransportTasks.SingleAsync(); task.TaskStatus = "ASSIGNED"; await other.SaveChangesAsync();
        }
        var result = await f.Tasks().UpdateAsync(1, new() { WarehouseId = 1, PickupStationId = 1,
            DeliveryStationId = 2, PayloadWeightKg = 2, PriorityLevel = 3 });
        Assert.Equal(TransportTaskResultStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task StartAckTimeoutDoesNotLeaveRobotPermanentlyBusy()
    {
        using var f = new Fixture(); f.Assign();
        f.Db.Missions.Single().MissionStatus = "INITIALIZING";
        f.Db.Missions.Single().CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        f.Db.TransportTasks.Single().TaskStatus = "ASSIGNED"; await f.Db.SaveChangesAsync();
        await new RobotHealthMonitor(f.Db, f.Config, f.Interventions).CheckAsync(DateTime.UtcNow);
        Assert.Equal("FAILED", f.Db.Missions.Single().MissionStatus);
        Assert.Equal("ACTIVE", f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Contains(f.Db.RobotCommandOutbox, c => c.Payload.Contains("PAUSE_TASK"));
        Assert.Equal("START_ACK_TIMEOUT", f.Db.RobotIncidents.Single().IncidentType);
    }

    [Fact]
    public async Task PausedInitializingMissionDoesNotExpireStartAck()
    {
        using var f = new Fixture(); f.Assign();
        f.Db.Missions.Single().MissionStatus = "INITIALIZING";
        f.Db.Missions.Single().CreatedAt = DateTime.UtcNow.AddMinutes(-2); await f.Db.SaveChangesAsync();
        await f.Interventions.Act(1, "pause", new("Check safety"), 42, default);
        await new RobotHealthMonitor(f.Db, f.Config, f.Interventions).CheckAsync(DateTime.UtcNow);
        Assert.Equal("INITIALIZING", f.Db.Missions.Single().MissionStatus); Assert.Empty(f.Db.RobotIncidents);
    }

    [Fact]
    public async Task CriticalQueuedTaskCanStillBeDispatched()
    {
        using var f = new Fixture(); f.Db.TransportTasks.Single().TaskStatus = "CRITICAL";
        await f.Db.SaveChangesAsync();
        var manual = new TaskDispatchService(f.Db, f.Config, NullLogger<TaskDispatchService>.Instance);
        Assert.Equal(TransportTaskResultStatus.Success, (await manual.AssignManualAsync(1, 1, 42)).Status);
    }

    [Fact]
    public async Task InvalidatedStationCannotBeUsedForNewAssignment()
    {
        using var f = new Fixture(); f.Db.Stations.Single(s => s.StationId == 2).StationType = "CHARGING";
        await f.Db.SaveChangesAsync();
        var result = await new TaskDispatchService(f.Db, f.Config, NullLogger<TaskDispatchService>.Instance).AssignManualAsync(1, 1, 42);
        Assert.Equal(TransportTaskResultStatus.Conflict, result.Status); Assert.Empty(f.Db.TaskAssignments);
    }

    [Fact]
    public async Task AutoTaskHasQrRouteBeforeDispatcherCanAssignIt()
    {
        using var f = new Fixture();
        f.Config["DemoRoute:Enabled"] = "true"; f.Config["AutoDispatch:EnableOnCreate"] = "true";
        var map = f.Navigation.GetMap();
        map.Cells[2][2] = new() { Type = "qr", Label = "QR0", ArucoId = 0, MarkerYawDeg = 0, MarkerSizeM = 0.04 };
        map.Cells[2][3] = new() { Type = "qr", Label = "QR1", ArucoId = 1, MarkerYawDeg = 0, MarkerSizeM = 0.04 };
        map.Links.Add(["QR0", "QR1"]); Assert.True(f.Navigation.ReplaceMap(map).Success);
        var result = await f.Tasks().CreateAsync(new() { WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2,
            DispatchMode = "AUTO", PayloadWeightKg = 1, PickupArUcoId = 0, DeliveryArUcoId = 1 }, 42);
        Assert.Equal(TransportTaskResultStatus.Success, result.Status);
        Assert.Equal("AUTO", result.Value!.DispatchMode); Assert.Equal("QUEUED", result.Value.TaskStatus);
        Assert.Equal(1, f.Routes.GetTaskSelection(result.Value.TaskId)!.DeliveryArUcoId);
        Assert.Empty(f.Db.TaskAssignments); // no fresh camera localization; never assign blindly
    }

    [Fact]
    public async Task QrPilotCreationRejectsMissingRouteBeforePersistingTask()
    {
        using var f = new Fixture(); f.Config["DemoRoute:Enabled"] = "true";
        var result = await f.Tasks().CreateAsync(new() { WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2,
            DispatchMode = "MANUAL", PayloadWeightKg = 1 }, 42);
        Assert.Equal(TransportTaskResultStatus.ValidationFailed, result.Status); Assert.Single(f.Db.TransportTasks);
    }

    [Fact]
    public async Task NewLowPriorityTaskCannotJumpAheadOfDueHighPriorityTask()
    {
        using var f = new Fixture(); f.Config["AutoDispatch:EnableOnCreate"] = "true";
        var old = f.Db.TransportTasks.Single(); old.DispatchMode = "AUTO"; old.PriorityLevel = 3;
        old.CreatedAt = DateTime.UtcNow.AddMinutes(-1); await f.Db.SaveChangesAsync();
        var result = await f.Tasks().CreateAsync(new() { WarehouseId = 1, PickupStationId = 1, DeliveryStationId = 2,
            DispatchMode = "AUTO", PayloadWeightKg = 1, PriorityLevel = 1 }, 42);
        Assert.Equal(TransportTaskResultStatus.Success, result.Status);
        Assert.Equal("QUEUED", result.Value!.TaskStatus);
        Assert.Equal(1, f.Db.TaskAssignments.Single().TaskId);
    }

    [Fact]
    public async Task LostHeartbeatFailsMissionAndRetainsLoadLockWithDurableStop()
    {
        using var f = new Fixture(); f.Assign();
        var robot = f.Db.Robots.Single(); robot.LastTelemetryTime = robot.LastHeartbeatTime = DateTime.UtcNow.AddMinutes(-2);
        await f.Db.SaveChangesAsync();
        await new RobotHealthMonitor(f.Db, f.Config, f.Interventions).CheckAsync(DateTime.UtcNow);
        Assert.Equal("OFFLINE", robot.ConnectionStatus); Assert.Equal("ERROR", robot.OperationalStatus);
        Assert.Equal("FAILED", f.Db.Missions.Single().MissionStatus);
        Assert.Equal("FAILED", f.Db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Contains(f.Db.RobotCommandOutbox, c => c.Payload.Contains("PAUSE_TASK"));
        Assert.Single(f.Db.RobotIncidents); Assert.Single(f.Db.Notifications);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(19, false)]
    [InlineData(20, false)]
    public async Task CriticalBatteryBoundaryAndAlertDeduplication(int battery, bool stops)
    {
        using var f = new Fixture(); f.Assign(); f.Db.Robots.Single().BatteryPercent = battery; await f.Db.SaveChangesAsync();
        var monitor = new RobotHealthMonitor(f.Db, f.Config, f.Interventions);
        await monitor.CheckAsync(DateTime.UtcNow); await monitor.CheckAsync(DateTime.UtcNow);
        Assert.Equal(stops ? "FAILED" : "EXECUTING", f.Db.TransportTasks.Single().TaskStatus);
        Assert.Equal("ACTIVE", f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal(battery < 20 ? 1 : 0, f.Db.Notifications.Count());
        Assert.Equal(stops ? 2 : 1, f.Db.RobotCommandOutbox.Count());
    }

    [Fact]
    public async Task CompletedEventRetrySurvivesLostInMemoryRoute()
    {
        using var f = new Fixture(); f.Assign(); f.Db.Missions.Single().MissionStatus = "COMPLETED";
        f.Db.TransportTasks.Single().TaskStatus = "COMPLETED"; f.Db.TaskAssignments.Single().AssignmentStatus = "COMPLETED";
        await f.Db.SaveChangesAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DemoRoute:Enabled"] = "true" }).Build();
        var result = await new MissionLifecycleService(f.Db, config, f.Routes, f.Interventions)
            .HandleAsync("R01", new() { Event = "MISSION_COMPLETED", TaskId = 1, AssignmentId = 1, MissionId = 1 });
        Assert.Equal(MissionEventResult.Duplicate, result);
    }

    [Fact]
    public async Task ProductionCompletionRequiresDeliveryVerification()
    {
        using var f = new Fixture(); f.Assign();
        var result = await new MissionLifecycleService(f.Db, f.Config, f.Routes, f.Interventions)
            .HandleAsync("R01", new() { Event = "MISSION_COMPLETED", TaskId = 1, AssignmentId = 1, MissionId = 1 });
        Assert.Equal(MissionEventResult.Rejected, result); Assert.Equal("ACTIVE", f.Db.TaskAssignments.Single().AssignmentStatus);
    }

    [Fact]
    public void RobotMovementTestEndpointRequiresAdmin()
    {
        Assert.Equal(AppRoles.Admin, typeof(RobotTestController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single().Roles);
    }

    [Fact]
    public void OutboxAllowsSafetyControlsButKeepsMissionUniqueness()
    {
        using var f = new Fixture();
        var indexes = f.Db.Model.FindEntityType(typeof(RobotCommandOutbox))!.GetIndexes();
        Assert.Contains(indexes, i => !i.IsUnique && i.Properties.Single().Name == "AssignmentId");
        Assert.Contains(indexes, i => i.IsUnique && i.GetFilter()!.Contains("EXECUTE_TASK"));
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AuditTests"; public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = ""; public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class Hub : IHubContext<FleetHub>
    { public IHubClients Clients { get; } = new SinkClients(); public IGroupManager Groups => throw new NotSupportedException(); }
    private sealed class SinkClients : IHubClients
    {
        private static readonly IClientProxy Sink = new Proxy(); public IClientProxy All => Sink;
        public IClientProxy AllExcept(IReadOnlyList<string> x) => Sink; public IClientProxy Client(string x) => Sink;
        public IClientProxy Clients(IReadOnlyList<string> x) => Sink; public IClientProxy Group(string x) => Sink;
        public IClientProxy GroupExcept(string x, IReadOnlyList<string> y) => Sink; public IClientProxy Groups(IReadOnlyList<string> x) => Sink;
        public IClientProxy User(string x) => Sink; public IClientProxy Users(IReadOnlyList<string> x) => Sink;
    }
    private sealed class Proxy : IClientProxy
    { public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default) => Task.CompletedTask; }
}
