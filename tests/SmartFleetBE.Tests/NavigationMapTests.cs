using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class NavigationMapTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ServiceProvider Database(bool existingMap = false)
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<SmartFleetDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<INavigationMapStore, DatabaseNavigationMapStore>();
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        db.Warehouses.Add(new Warehouse { WarehouseId = 1, WarehouseCode = "WH01", WarehouseName = "Test", IsActive = true });
        if (existingMap)
            db.MapConfigurations.Add(new MapConfiguration { MapId = 1, WarehouseId = 1, MapVersion = "slam-v1",
                MapSource = "LIDAR_SLAM", CoordinateSystem = "LOCAL_METRIC", IsActive = true,
                MapMetadataJson = "{\"slam\":{\"origin\":42}}" });
        db.SaveChanges();
        return provider;
    }

    private static NavigationCoordinator Coordinator(Environment? env = null, INavigationMapStore? store = null) =>
        new(env ?? new Environment(), new ConfigurationBuilder().Build(),
            NullLogger<NavigationCoordinator>.Instance, store);

    private static QrDemoRouteService Routes(NavigationCoordinator nav) => new(nav, new Environment(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["DemoRoute:Enabled"] = "true" }).Build(), NullLogger<QrDemoRouteService>.Instance);

    private static WarehouseMapDocument QrMap(NavigationCoordinator nav)
    {
        var map = nav.GetMap();
        map.Cells = Enumerable.Range(0, 18).Select(_ => Enumerable.Range(0, 28)
            .Select(_ => new WarehouseMapCell { Type = "floor" }).ToList()).ToList();
        map.Links = [];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                var id = r * 3 + c;
                map.Cells[r + 5][c + 10] = new WarehouseMapCell
                    { Type = "qr", Label = $"QR{id}", ArucoId = id, IsIntersection = id == 4, MarkerSizeM = 0.04, MarkerYawDeg = 0 };
                if (c > 0) map.Links.Add([$"QR{id - 1}", $"QR{id}"]);
                if (r > 0) map.Links.Add([$"QR{id - 3}", $"QR{id}"]);
            }
        return map;
    }

    [Fact]
    public void ImportsLegacyFileOnceWithoutReplacingSlamMetadata()
    {
        using var provider = Database(existingMap: true);
        var env = new Environment();
        var legacy = Coordinator(env);
        Assert.True(legacy.ReplaceMap(QrMap(legacy)).Success);
        var store = provider.GetRequiredService<INavigationMapStore>();
        var nav = Coordinator(env, store);
        using var scope = provider.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>().MapConfigurations.Single();
        var json = JsonNode.Parse(row.MapMetadataJson!)!;
        Assert.Equal(42, json["slam"]!["origin"]!.GetValue<int>());
        Assert.Equal(legacy.GetMap().Revision, json["navigationMap"]!["revision"]!.GetValue<int>());
        Assert.Equal(9, Routes(nav).GetMapReadiness().QrNodeCount);
        Assert.Equal("slam-v1", row.MapVersion);
    }

    [Fact]
    public void TrafficPreviewNeverMutatesLiveQueues()
    {
        var nav = Coordinator(); Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        var preview = nav.PreviewScan(1, "R01", new() { CurrentArUcoId = 3, NextArUcoId = 4 });
        Assert.True(preview.Result.Success); Assert.Equal(1, preview.Intersection!.WaitingCount);
        Assert.Equal(0, nav.GetIntersection(4)!.TotalRelevantCount);
    }

    [Fact]
    public void ActiveAssignmentBlocksMapEditsOutsideIntersections()
    {
        using var provider = Database();
        var store = provider.GetRequiredService<INavigationMapStore>();
        var nav = Coordinator(store: store);
        using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        db.TaskAssignments.Add(new TaskAssignment { TaskId = 1, AssignmentStatus = "ACTIVE", AssignmentType = "MANUAL",
            Task = new TransportTask { TaskId = 1, WarehouseId = 1, TaskStatus = "EXECUTING", TaskTrackingCode = "T1" } });
        // This fixture validates only the guard; unused relational fields are not needed.
        db.Entry(db.TaskAssignments.Local.Single()).Property(a => a.RobotId).CurrentValue = 1;
        db.Robots.Add(new Robot { RobotId = 1, RobotCode = "R01", RobotType = "PHYSICAL", Model = "Test",
            ControlSoftware = "Test", MqttClientIdentifier = "test-r01", ConnectionStatus = "ONLINE", OperationalStatus = "BUSY" });
        db.SaveChanges();
        var before = nav.GetMap().Revision; var result = nav.ReplaceMap(QrMap(nav));
        Assert.False(result.Success); Assert.True(result.Conflict); Assert.Equal(before, nav.GetMap().Revision);
    }

    [Fact]
    public void QrDispatchCannotUseAnotherWarehousesMap()
    {
        using var provider = Database(); var nav = Coordinator(store: provider.GetRequiredService<INavigationMapStore>());
        var routes = Routes(nav);
        Assert.False(routes.CanDispatchTask(1, "R2", out var reason, 2)); Assert.Contains("warehouse", reason);
    }

    [Fact]
    public void DatabaseMapSurvivesMissingLocalFileAndAllQrPairsCanBeRouted()
    {
        using var provider = Database();
        var store = provider.GetRequiredService<INavigationMapStore>();
        var first = Coordinator(store: store);
        Assert.True(first.ReplaceMap(QrMap(first)).Success);
        // A fresh content root has no local map file. PostgreSQL remains authoritative.
        var reloaded = Coordinator(store: store);
        Assert.Equal(first.GetMap().Revision, reloaded.GetMap().Revision);
        var routes = Routes(reloaded);
        var validation = routes.GetMapReadiness();
        Assert.True(validation.ReadyForRouting);
        Assert.Equal(9, validation.QrNodeCount);
        Assert.Equal(12, validation.CorridorCount);
        for (int start = 0; start < 9; start++)
            for (int end = 0; end < 9; end++)
            {
                if (start == end) continue;
                var id = start * 9 + end + 1;
                // One test robot is repositioned between independent route cases.
                var robot = new Robot { RobotCode = "TEST", WarehouseId = 1 };
                Assert.True(routes.ReceiveProgress(robot.RobotCode, new()
                    { Event = "READY", ArucoId = start, MapRevision = reloaded.GetMap().Revision,
                      HeadingDeg = 0, LocalizationSource = "ARUCO_IMU", ReprojectionErrorPx = 0.1, StableFrames = 5 }).Accepted);
                if (start == 4 || end == 4)
                {
                    Assert.Throws<ArgumentException>(() => routes.ConfigureTask(id, start, end));
                    continue; // Transfer stations cannot stop inside a reserved intersection.
                }
                routes.ConfigureTask(id, start, end);
                var command = JsonNode.Parse(routes.BuildMqttCommand(new TaskAssignment
                    { AssignmentId = id, TaskId = id, Robot = robot, Mission = new Mission { MissionId = id } }))!;
                Assert.Equal(start, command["waypoints"]![0]!["arucoId"]!.GetValue<int>());
                var waypoints = command["waypoints"]!.AsArray();
                Assert.Equal(end, waypoints.Last()!["arucoId"]!.GetValue<int>());
                Assert.Equal(reloaded.GetMap().Revision, command["mapRevision"]!.GetValue<int>());
            }
    }

    [Fact]
    public void StaleBackendCannotOverwriteNewerDatabaseRevision()
    {
        using var provider = Database();
        var store = provider.GetRequiredService<INavigationMapStore>();
        var first = Coordinator(store: store);
        var second = Coordinator(store: store);
        Assert.True(first.ReplaceMap(QrMap(first)).Success);
        var result = second.ReplaceMap(second.GetMap());
        Assert.False(result.Success);
        Assert.True(result.Conflict);
        Assert.Equal(first.GetMap().Revision, second.GetMap().Revision);
        Assert.Equal(9, Routes(second).GetMapReadiness().QrNodeCount);
    }

    [Fact]
    public void FailedDatabaseWriteDoesNotChangeLiveMapOrReportSuccess()
    {
        var nav = Coordinator(store: new UnavailableStore());
        var before = JsonSerializer.Serialize(nav.GetMap(), JsonOptions);
        var result = nav.ReplaceMap(QrMap(nav));
        Assert.False(result.Success);
        Assert.True(result.Unavailable);
        Assert.Equal(before, JsonSerializer.Serialize(nav.GetMap(), JsonOptions));
    }

    [Fact]
    public void InvalidQrIdsCannotBeSaved()
    {
        using var provider = Database();
        var store = provider.GetRequiredService<INavigationMapStore>();
        var nav = Coordinator(store: store);
        var map = QrMap(nav);
        map.Cells[5][10].ArucoId = 51;
        Assert.False(nav.ReplaceMap(map).Success);
        Assert.Equal(1, Coordinator(store: store).GetMap().Revision);
    }

    [Fact]
    public void IntersectionFlagIsPersistedByItsOwnEndpoint()
    {
        using var provider = Database();
        var store = provider.GetRequiredService<INavigationMapStore>();
        var nav = Coordinator(store: store);
        Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        Assert.True(nav.MarkIntersection(4, false).Success);
        Assert.Empty(Coordinator(store: store).GetIntersections());
    }

    [Fact]
    public void ReadinessExplainsMissingDisconnectedAndDiagonalQrMaps()
    {
        var nav = Coordinator();
        Assert.False(Routes(nav).GetMapReadiness().ReadyForRouting);
        var map = QrMap(nav);
        map.Links = [["QR0", "QR4"]];
        Assert.True(nav.ReplaceMap(map).Success);
        var validation = Routes(nav).GetMapReadiness();
        Assert.False(validation.ReadyForRouting);
        Assert.Contains(validation.Issues, x => x.Contains("horizontal or vertical"));
        Assert.Contains(validation.Issues, x => x.Contains("disconnected"));
    }

    [Fact]
    public void MultipleWarehousesRequireExplicitConfiguration()
    {
        using var provider = Database();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SmartFleetDbContext>();
        db.Warehouses.Add(new Warehouse { WarehouseId = 2, WarehouseCode = "WH02", WarehouseName = "Other", IsActive = true });
        db.SaveChanges();
        Assert.Throws<InvalidOperationException>(() => Coordinator(store: provider.GetRequiredService<INavigationMapStore>()));
    }

    private sealed class UnavailableStore : INavigationMapStore
    {
        public WarehouseMapDocument LoadOrCreate(WarehouseMapDocument legacy) => legacy;
        public void Save(WarehouseMapDocument map, int revision) => throw new TimeoutException("Test database unavailable");
    }

    [Theory]
    [InlineData(0, "ARUCO_IMU", 0.1, 5)]
    [InlineData(2, "MANUAL", 0.1, 5)]
    [InlineData(2, "ARUCO_IMU", 3.0, 5)]
    [InlineData(2, "ARUCO_IMU", 0.1, 4)]
    public void InvalidHeadingEvidenceCannotMakeRobotDispatchable(int revision, string source, double error, int frames)
    {
        var nav = Coordinator();
        Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        var routes = Routes(nav);
        var result = routes.ReceiveProgress("R01", new() { Event = "READY", ArucoId = 0,
            MapRevision = revision, HeadingDeg = 0, LocalizationSource = source,
            ReprojectionErrorPx = error, StableFrames = frames });
        Assert.False(result.Accepted);
        Assert.False(routes.HasFreshLocalization("R01"));
        Assert.Empty(routes.GetRobotPositions());
    }

    [Fact]
    public void LocalizationLossAndMapChangeInvalidateAValidRobotPose()
    {
        var nav = Coordinator();
        Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        var routes = Routes(nav);
        var ready = new QrDemoRouteService.RouteProgressReport { Event = "READY", ArucoId = 0,
            MapRevision = nav.GetMap().Revision, HeadingDeg = -179.9, LocalizationSource = "ARUCO_IMU",
            ReprojectionErrorPx = 0.1, StableFrames = 5 };
        Assert.True(routes.ReceiveProgress("R01", ready).Accepted);
        Assert.True(routes.HasFreshLocalization("R01"));
        Assert.True(routes.ReceiveProgress("R01", new() { Event = "LOCALIZATION_LOST" }).Accepted);
        Assert.False(routes.HasFreshLocalization("R01"));
        Assert.True(routes.ReceiveProgress("R01", ready).Accepted);
        Assert.True(nav.ReplaceMap(nav.GetMap()).Success);
        Assert.False(routes.HasFreshLocalization("R01"));
        Assert.Empty(routes.GetRobotPositions());
    }

    [Fact]
    public void DispatchRejectsAnUncommissionedDeliveryMarkerBeforeAssigning()
    {
        var nav = Coordinator();
        var map = QrMap(nav);
        map.Cells[7][12].MarkerYawDeg = null; // QR8 is the requested delivery.
        Assert.True(nav.ReplaceMap(map).Success);
        var routes = Routes(nav);
        Assert.True(routes.ReceiveProgress("R01", new() { Event = "READY", ArucoId = 0,
            MapRevision = nav.GetMap().Revision, HeadingDeg = 0, LocalizationSource = "ARUCO_IMU",
            ReprojectionErrorPx = 0.1, StableFrames = 5 }).Accepted);
        routes.ConfigureTask(9001, 0, 8);
        Assert.False(routes.CanDispatchTask(9001, "R01", out var reason));
        Assert.Contains("Commission every QR", reason);
        Assert.Empty(routes.GetMissions());
    }

    [Fact]
    public void PlannerAvoidsAnIdleRobotsCameraCheckpoint()
    {
        var nav = Coordinator(); Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        var routes = Routes(nav);
        foreach (var (code, qr) in new[] { ("R01", 0), ("R02", 1) })
            Assert.True(routes.ReceiveProgress(code, new() { Event = "READY", ArucoId = qr,
                MapRevision = nav.GetMap().Revision, HeadingDeg = 0, LocalizationSource = "ARUCO_IMU",
                ReprojectionErrorPx = .1, StableFrames = 5 }).Accepted);
        routes.ConfigureTask(100, 0, 2);
        var command = JsonNode.Parse(routes.BuildMqttCommand(new TaskAssignment {
            AssignmentId = 100, TaskId = 100, Robot = new Robot { RobotCode = "R01" }, Mission = new Mission { MissionId = 100 } }))!;
        var ids = command["waypoints"]!.AsArray().Select(w => w!["arucoId"]!.GetValue<int>()).ToArray();
        Assert.DoesNotContain(1, ids); Assert.Equal(2, ids[^1]);
        routes.ConfigureTask(101, 0, 1);
        Assert.False(routes.CanDispatchTask(101, "R01", out var reason));
        Assert.Contains("occup", reason);
    }

    [Fact]
    public void NormalCorridorStopsBeforeAnOccupiedQrAndResumesOnlyAfterItMoves()
    {
        var nav = Coordinator(); Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        nav.RecordCameraCheckpoint("R02", 1);
        nav.RecordCameraCheckpoint("R01", 0);
        nav.OnLidar(1, "R01", "CLEAR");
        var blocked = nav.OnScan(1, "R01", new() { CurrentArUcoId = 0, NextArUcoId = 1 });
        Assert.Equal("QR_OCCUPIED_BY_OTHER_ROBOT", Assert.Single(blocked.Directives).Reason);
        nav.RecordCameraCheckpoint("R02", 2);
        Assert.Equal("GO", Assert.Single(nav.OnLidar(1, "R01", "CLEAR").Directives).Mode);
        Assert.Contains(1, nav.GetBlockedQrIds("R02"));
    }

    [Fact]
    public void ClaimedCorridorSurvivesDuplicateCameraReportAndBackendRestart()
    {
        var env = new Environment(); var nav = Coordinator(env);
        Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        nav.RecordCameraCheckpoint("R02", 1);
        nav.OnLidar(2, "R02", "CLEAR");
        Assert.Equal("GO", Assert.Single(nav.OnScan(2, "R02", new() { CurrentArUcoId = 1, NextArUcoId = 2 }).Directives).Mode);
        nav.RecordCameraCheckpoint("R02", 1); // Replay is not an EXIT.
        var reloaded = Coordinator(env);
        Assert.Contains(1, reloaded.GetBlockedQrIds("R01"));
        Assert.Contains(2, reloaded.GetBlockedQrIds("R01"));
        Assert.Empty(reloaded.GetBlockedQrIds("r02"));
        reloaded.RecordCameraCheckpoint("R02", 2);
        Assert.DoesNotContain(1, reloaded.GetBlockedQrIds("R01"));
        Assert.Contains(2, reloaded.GetBlockedQrIds("R01"));
    }

    [Fact]
    public void TwoRobotsCannotClaimTheSameNonIntersectionDestination()
    {
        var nav = Coordinator(); Assert.True(nav.ReplaceMap(QrMap(nav)).Success);
        nav.RecordCameraCheckpoint("R01", 0); nav.RecordCameraCheckpoint("R02", 2);
        nav.OnLidar(1, "R01", "CLEAR"); nav.OnLidar(2, "R02", "CLEAR");
        Assert.Equal("GO", Assert.Single(nav.OnScan(1, "R01", new() { CurrentArUcoId = 0, NextArUcoId = 1 }).Directives).Mode);
        Assert.Equal("STOP", Assert.Single(nav.OnScan(2, "R02", new() { CurrentArUcoId = 2, NextArUcoId = 1 }).Directives).Mode);
    }

    private sealed class Environment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MapTests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "smartfleet-map-tests-" + Guid.NewGuid());
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
