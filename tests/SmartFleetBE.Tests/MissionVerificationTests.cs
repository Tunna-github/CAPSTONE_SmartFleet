using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Constants;
using SmartFleetBE.Controllers;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class MissionVerificationTests
{
    // Set SMARTFLEET_TEST_PG only in the local test runner, never in source.
    // PostgreSQL runs use isolated schemas and never modify operational tables.
    private sealed class Fixture : IDisposable
    {
        private readonly string temp = Path.Combine(Path.GetTempPath(), "fe07-" + Guid.NewGuid().ToString("N"));
        private readonly string? schema;
        private readonly DbContextOptions<SmartFleetDbContext> options;
        public SmartFleetDbContext Db { get; private set; }
        public NavigationCoordinator Nav { get; private set; }
        public QrDemoRouteService Routes { get; private set; }
        public IConfiguration Config { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["DemoRoute:Enabled"]="true", ["Verification:MaxQrAttempts"]="3" }).Build();
        public Fixture()
        {
            var builder = new DbContextOptionsBuilder<SmartFleetDbContext>();
            if (Environment.GetEnvironmentVariable("SMARTFLEET_TEST_PG") is { Length: >0 } connection)
            {
                schema = "fe07_test_" + Guid.NewGuid().ToString("N");
                var cs = new Npgsql.NpgsqlConnectionStringBuilder(connection) { SearchPath = schema, Pooling = false };
                builder.UseNpgsql(cs.ConnectionString);
                options = builder.Options; Db = new(options);
                SchemaCommand(create:true);
                Db.Database.ExecuteSqlRaw(Db.Database.GenerateCreateScript());
            }
            else
            {
                builder.UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false))
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
                options = builder.Options; Db = new(options);
            }
            Nav = null!; Routes = null!; ResetRoutes();
            var now = DateTime.UtcNow;
            Db.Warehouses.Add(new Warehouse { WarehouseId=1, WarehouseCode="W1", WarehouseName="Test", IsActive=true, CreatedAt=now });
            Db.Zones.Add(new Zone { ZoneId=1, WarehouseId=1, ZoneCode="Z1", ZoneName="Test", ZoneType="STORAGE", IsActive=true, CreatedAt=now });
            Db.Stations.AddRange(new Station { StationId=1, ZoneId=1, StationCode="P1", StationName="Pickup", StationType="PICKUP", QrCodePayload="STATION:P1", IsActive=true, CreatedAt=now },
                new Station { StationId=2, ZoneId=1, StationCode="D1", StationName="Delivery", StationType="DELIVERY", QrCodePayload="STATION:D1", IsActive=true, CreatedAt=now });
            Db.Users.Add(new User { UserId=1, Username="operator", Email="operator@test.invalid", FullName="Test Operator", PasswordHash="test-only", IsActive=true, CreatedAt=now });
            Db.Robots.Add(new Robot { RobotId=1, WarehouseId=1, RobotCode="R01", RobotType="SIMULATED", Model="Test", ControlSoftware="Test",
                MqttClientIdentifier="test-r01", OperationalStatus="BUSY", ConnectionStatus="ONLINE", IsActive=true,
                BatteryPercent=80, LastHeartbeatTime=now, LastTelemetryTime=now, CreatedAt=now });
            Db.TransportTasks.Add(new TransportTask { TaskId=1, TaskTrackingCode="T1", WarehouseId=1, PickupStationId=1,
                DeliveryStationId=2, CreatedBy=1, PackageCode="BOX-1", PriorityLevel=2, PayloadWeightKg=1,
                TaskStatus="EXECUTING", DispatchMode="MANUAL", CreatedAt=now });
            Db.TaskAssignments.Add(new TaskAssignment { AssignmentId=1, TaskId=1, RobotId=1, AssignmentType="MANUAL", AssignmentStatus="ACTIVE", AssignedAt=now });
            Db.Missions.Add(new Mission { MissionId=1, AssignmentId=1, MissionCode="M1", MissionStatus="NAVIGATING_TO_PICKUP", CreatedAt=now });
            Db.SaveChanges();
            Db.StationCheckpoints.AddRange(new() { StationId=1, WarehouseId=1, ArucoId=1 }, new() { StationId=2, WarehouseId=1, ArucoId=2 });
            Db.SaveChanges();
            Routes.ConfigureTask(1,1,2);
            Assert.True(Routes.ReceiveProgress("R01", new() { Event="READY", ArucoId=0, MapRevision=Nav.GetMap().Revision,
                HeadingDeg=0, LocalizationSource="ARUCO_IMU", ReprojectionErrorPx=.1, StableFrames=5 }).Accepted);
            MissionExecutionService.PrepareAsync(Db, Routes, Db.TaskAssignments.Single(), default).GetAwaiter().GetResult();
            Db.SaveChanges();
        }
        private void ResetRoutes()
        {
            var env = new Env { ContentRootPath=temp };
            Nav = new NavigationCoordinator(env, Config, NullLogger<NavigationCoordinator>.Instance);
            if (Nav.GetMap().Cells.SelectMany(r=>r).Count(c=>c.ArucoId.HasValue)<3)
            {
                var map = Nav.GetMap();
                map.Cells = Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,28).Select(_=>new WarehouseMapCell { Type="floor" }).ToList()).ToList();
                for(var i=0;i<3;i++) map.Cells[5][10+i]=new WarehouseMapCell { Type="qr", Label=$"QR{i}", ArucoId=i, MarkerYawDeg=0, MarkerSizeM=.04 };
                map.Links = [["QR0","QR1"],["QR1","QR2"]];
                Assert.True(Nav.ReplaceMap(map).Success);
            }
            Routes = new(Nav, env, Config, NullLogger<QrDemoRouteService>.Instance);
        }
        public MissionExecutionService Service() => new(Db, Routes, Config);
        public SmartFleetDbContext OpenDb(SaveChangesInterceptor? interceptor=null) => new(new DbContextOptionsBuilder<SmartFleetDbContext>(options)
            .AddInterceptors(interceptor==null ? [] : [interceptor]).Options);
        public MissionLifecycleService Lifecycle() => new(Db, Config, Routes, new(Db,Routes,Config));
        public QrDemoRouteService.RouteProgressReport Report(int index, string? eventId=null, string kind="WAYPOINT_REACHED") => new()
            { Event=kind, EventId=eventId??Guid.NewGuid().ToString(), MissionId=1, ArucoId=index, WaypointIndex=index,
                MapRevision=Nav.GetMap().Revision, RobotStopped=true };
        public async Task ReachPickup()
        { Assert.True((await Service().ProgressAsync("R01",Report(0),default)).Accepted); Assert.True((await Service().ProgressAsync("R01",Report(1),default)).Accepted); }
        public void Restart() { Db.Dispose(); Db=new(options); ResetRoutes(); }
        public void Dispose()
        {
            if(schema!=null) SchemaCommand(create:false);
            Db.Dispose(); if(Directory.Exists(temp)) Directory.Delete(temp,true);
        }
        private void SchemaCommand(bool create)
        {
            // Identifier is a locally generated GUID and is quoted as an identifier, never a value.
            using var quoting=new Npgsql.NpgsqlCommandBuilder();
            Db.Database.OpenConnection();
            using var command=Db.Database.GetDbConnection().CreateCommand();
            command.CommandText=(create?"CREATE SCHEMA ":"DROP SCHEMA ")+quoting.QuoteIdentifier(schema!)+(create?"":" CASCADE");
            command.ExecuteNonQuery(); Db.Database.CloseConnection();
        }
    }
    private static VerificationRequest Request(string stage="PICKUP", string? id=null) => new(id??Guid.NewGuid().ToString(),stage,"QRCODE",
        stage=="PICKUP"?"STATION:P1":"STATION:D1","BOX-1",true,null);

    [Fact]
    public async Task HappyPathRequiresBothVerificationsAndRobotCompletion()
    {
        using var f=new Fixture(); await f.ReachPickup();
        Assert.Equal("AT_PICKUP",f.Db.Missions.Single().MissionStatus);
        Assert.False((await f.Service().ProgressAsync("R01",f.Report(2),default)).Accepted);
        Assert.True((await f.Service().VerifyAsync(1,1,Request(),default)).Accepted);
        Assert.Equal("CONTINUE",(await f.Service().ProgressAsync("R01",f.Report(1,kind:"VERIFICATION_STATUS"),default)).Action);
        Assert.True((await f.Service().ProgressAsync("R01",f.Report(2),default)).Accepted);
        var completion=new SmartFleetBE.DTOs.RobotMissionEvent { Event="MISSION_COMPLETED",TaskId=1,AssignmentId=1,MissionId=1 };
        Assert.Equal(MissionEventResult.Rejected,await f.Lifecycle().HandleAsync("R01",completion));
        Assert.True((await f.Service().VerifyAsync(1,1,Request("DELIVERY"),default)).Accepted);
        Assert.Equal("ACTIVE",f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal("COMPLETE",(await f.Service().ProgressAsync("R01",f.Report(2,kind:"VERIFICATION_STATUS"),default)).Action);
        Assert.Equal(MissionEventResult.Applied,await f.Lifecycle().HandleAsync("R01",completion));
        Assert.Equal("COMPLETED",f.Db.TransportTasks.Single().TaskStatus);
        Assert.Equal("AVAILABLE",f.Db.Robots.Single().OperationalStatus);
        Assert.Equal(3,f.Db.MissionWaypoints.Count(w=>w.IsReached));
        Assert.Equal(2,f.Db.DeliveryVerifications.Count());
    }

    [Fact]
    public async Task WrongQrOrPackageRecordsFailuresWithoutReleasingLoadAndAllowsReasonedManualVerification()
    {
        using var f=new Fixture(); await f.ReachPickup();
        for(var i=0;i<3;i++) Assert.False((await f.Service().VerifyAsync(1,1,Request() with { ScannedPackageCode="WRONG" },default)).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,Request(),default));
        Assert.Equal("AT_PICKUP",f.Db.Missions.Single().MissionStatus); Assert.Equal("ACTIVE",f.Db.TaskAssignments.Single().AssignmentStatus);
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service().VerifyAsync(1,1,Request() with { Type="MANUAL" },default));
        Assert.True((await f.Service().VerifyAsync(1,1,Request() with { Type="MANUAL",ScannedQr=null,Note="Scanner damaged; goods checked physically." },default)).Accepted);
        Assert.Equal(4,f.Db.VerificationAttempts.Count()); Assert.Equal(1,f.Db.DeliveryVerifications.Count());
        Assert.Equal("MANUAL",f.Db.DeliveryVerifications.Single().VerificationType);
    }

    [Fact]
    public async Task LostVerificationResponseCanBeReplayedAfterRestartWithoutDuplicateTransfer()
    {
        using var f=new Fixture(); await f.ReachPickup(); var request=Request();
        Assert.True((await f.Service().VerifyAsync(1,1,request,default)).Accepted);
        f.Restart(); Assert.Null(f.Routes.GetMission(1));
        Assert.True((await f.Service().VerifyAsync(1,1,request,default)).Duplicate);
        Assert.Equal(1,f.Db.VerificationAttempts.Count()); Assert.Equal(1,f.Db.DeliveryVerifications.Count());
        Assert.Equal("CONTINUE",(await f.Service().ProgressAsync("R01",f.Report(1,kind:"VERIFICATION_STATUS"),default)).Action);
        Assert.NotNull(f.Routes.GetMission(1));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,request with { Note="changed" },default));
    }

    [Fact]
    public async Task WrongRobotQrOrderRevisionOrReusedEventCannotAdvanceCursor()
    {
        using var f=new Fixture();
        Assert.False((await f.Service().ProgressAsync("OTHER",f.Report(0),default)).Accepted);
        Assert.False((await f.Service().ProgressAsync("R01",f.Report(2),default)).Accepted);
        var report=f.Report(0); report.MapRevision=-1;
        Assert.False((await f.Service().ProgressAsync("R01",report,default)).Accepted);
        report=f.Report(0); Assert.True((await f.Service().ProgressAsync("R01",report,default)).Accepted);
        Assert.True((await f.Service().ProgressAsync("R01",report,default)).Accepted);
        var reuse=f.Report(1,report.EventId); Assert.False((await f.Service().ProgressAsync("R01",reuse,default)).Accepted);
        Assert.Equal(1,f.Db.MissionExecutions.Single().NextWaypointIndex);
        Assert.Equal(1,f.Db.RobotQrScanEvents.Count());
    }

    [Fact]
    public async Task VerificationRejectsEarlyMissingPhysicalConfirmationStaleOfflineOrHeldRobot()
    {
        using var f=new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,Request(),default));
        await f.ReachPickup();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Service().VerifyAsync(1,1,Request() with { PhysicalTransferConfirmed=false },default));
        f.Db.MissionExecutions.Single().StationPresenceAt=DateTime.UtcNow.AddMinutes(-1); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,Request(),default));
        await f.Service().ProgressAsync("R01",f.Report(1,kind:"VERIFICATION_STATUS"),default);
        f.Db.Robots.Single().ConnectionStatus="OFFLINE"; await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,Request(),default));
        f.Db.Robots.Single().ConnectionStatus="ONLINE";
        f.Db.TaskInterventions.Add(new TaskIntervention { TaskId=1,Paused=true,State="NORMAL",UpdatedAt=DateTime.UtcNow }); await f.Db.SaveChangesAsync();
        Assert.False((await f.Service().ProgressAsync("R01",f.Report(1,kind:"VERIFICATION_STATUS"),default)).Accepted);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service().VerifyAsync(1,1,Request(),default));
        Assert.Empty(f.Db.DeliveryVerifications);
    }

    [Fact]
    public async Task RestartWhileCarryingPreservesProgressVerificationAndOriginalStationPayload()
    {
        using var f=new Fixture(); await f.ReachPickup(); await f.Service().VerifyAsync(1,1,Request(),default);
        f.Db.Stations.Single(s=>s.StationId==2).QrCodePayload="EDITED"; await f.Db.SaveChangesAsync(); f.Restart();
        Assert.True((await f.Service().ProgressAsync("R01",f.Report(2),default)).Accepted);
        Assert.True((await f.Service().VerifyAsync(1,1,Request("DELIVERY"),default)).Accepted);
        var view=JsonSerializer.SerializeToElement(await f.Service().ViewAsync(1,default));
        Assert.True(view.GetProperty("PickupVerified").GetBoolean()); Assert.True(view.GetProperty("DeliveryVerified").GetBoolean());
        Assert.Equal(3,view.GetProperty("NextWaypointIndex").GetInt32());
    }

    [Fact]
    public async Task ActiveMissionAndItsViewKeepFrozenCodesAfterCatalogCutover()
    {
        using var f = new Fixture(); await f.ReachPickup();
        var frozenRoute = f.Db.MissionExecutions.Single().RoutePayloadJson;
        f.Db.Stations.Single(s => s.StationId == 1).QrCodePayload = "QR2";
        f.Db.Stations.Single(s => s.StationId == 2).QrCodePayload = "QR7";
        await f.Db.SaveChangesAsync();
        var view = JsonSerializer.SerializeToElement(await f.Service().ViewAsync(1, default));
        Assert.Equal("STATION:P1", view.GetProperty("PickupVerificationCode").GetString());
        Assert.Equal("STATION:D1", view.GetProperty("DeliveryVerificationCode").GetString());
        Assert.False((await f.Service().VerifyAsync(1, 1, Request() with { ScannedQr = "QR2" }, default)).Accepted);
        Assert.Equal("AT_PICKUP", f.Db.Missions.Single().MissionStatus);
        Assert.True((await f.Service().VerifyAsync(1, 1, Request(), default)).Accepted);
        Assert.True((await f.Service().ProgressAsync("R01", f.Report(2), default)).Accepted);
        Assert.False((await f.Service().VerifyAsync(1, 1, Request("DELIVERY") with { ScannedQr = "QR7" }, default)).Accepted);
        Assert.True((await f.Service().VerifyAsync(1, 1, Request("DELIVERY"), default)).Accepted);
        Assert.Equal("ACTIVE", f.Db.TaskAssignments.Single().AssignmentStatus);
        Assert.Equal(frozenRoute, f.Db.MissionExecutions.Single().RoutePayloadJson);
        Assert.All(f.Db.DeliveryVerifications, v => Assert.StartsWith("STATION:", v.ExpectedQrPayload));
    }

    [Fact]
    public async Task NewlyAssignedMissionUsesCanonicalCodesAndRejectsLegacyCodes()
    {
        using var f = new Fixture();
        f.Db.Stations.Single(s => s.StationId == 1).QrCodePayload = "QR2";
        f.Db.Stations.Single(s => s.StationId == 2).QrCodePayload = "QR7";
        f.Db.TaskAssignments.Single().AssignmentStatus = "COMPLETED";
        f.Db.Missions.Single().MissionStatus = "COMPLETED";
        f.Db.TransportTasks.Add(new() { TaskId = 2, TaskTrackingCode = "T2", WarehouseId = 1, PickupStationId = 1,
            DeliveryStationId = 2, CreatedBy = 1, PackageCode = "BOX-1", PriorityLevel = 2, PayloadWeightKg = 1,
            TaskStatus = "EXECUTING", DispatchMode = "MANUAL", CreatedAt = DateTime.UtcNow });
        f.Db.TaskAssignments.Add(new() { AssignmentId = 2, TaskId = 2, RobotId = 1, AssignmentType = "MANUAL", AssignmentStatus = "ACTIVE", AssignedAt = DateTime.UtcNow });
        f.Db.Missions.Add(new() { MissionId = 2, AssignmentId = 2, MissionCode = "M2", MissionStatus = "NAVIGATING_TO_PICKUP", CreatedAt = DateTime.UtcNow });
        await f.Db.SaveChangesAsync();
        await MissionExecutionService.PrepareAsync(f.Db, f.Routes, f.Db.TaskAssignments.Single(a => a.AssignmentId == 2), default);
        await f.Db.SaveChangesAsync();
        Assert.Equal("STATION:P1", f.Db.MissionExecutions.Single(e => e.MissionId == 1).ExpectedPickupQr);
        for (var index = 0; index <= 1; index++) {
            var report = f.Report(index); report.MissionId = 2;
            Assert.True((await f.Service().ProgressAsync("R01", report, default)).Accepted);
        }
        Assert.False((await f.Service().VerifyAsync(2, 1, Request(), default)).Accepted);
        Assert.True((await f.Service().VerifyAsync(2, 1, Request() with { ScannedQr = "QR2" }, default)).Accepted);
        var delivery = f.Report(2); delivery.MissionId = 2;
        Assert.True((await f.Service().ProgressAsync("R01", delivery, default)).Accepted);
        Assert.False((await f.Service().VerifyAsync(2, 1, Request("DELIVERY"), default)).Accepted);
        Assert.True((await f.Service().VerifyAsync(2, 1, Request("DELIVERY") with { ScannedQr = "QR7" }, default)).Accepted);
        var view = JsonSerializer.SerializeToElement(await f.Service().ViewAsync(2, default));
        Assert.Equal("QR2", view.GetProperty("PickupVerificationCode").GetString());
        Assert.Equal("QR7", view.GetProperty("DeliveryVerificationCode").GetString());
    }

    [Fact]
    public void OnlyWarehouseOperatorCanSubmitVerification()
    {
        var roles=typeof(MissionsController).GetMethod(nameof(MissionsController.Verify))!
            .GetCustomAttributes(typeof(AuthorizeAttribute),true).Cast<AuthorizeAttribute>().Single().Roles;
        Assert.Equal(AppRoles.WarehouseOperator,roles);
    }

    [Fact]
    public async Task ConcurrentVerificationRequestsRecordOnlyOnePhysicalTransfer()
    {
        using var f=new Fixture(); await f.ReachPickup();
        using var db1=f.OpenDb(); using var db2=f.OpenDb();
        async Task<bool> Send(SmartFleetDbContext db)
        {
            try { return (await new MissionExecutionService(db,f.Routes,f.Config).VerifyAsync(1,1,Request(),default)).Accepted; }
            catch(InvalidOperationException) { return false; }
        }
        var results=await Task.WhenAll(Send(db1),Send(db2));
        Assert.Single(results,x=>x);
        using var check=f.OpenDb(); Assert.Equal(1,await check.DeliveryVerifications.CountAsync());
        Assert.Equal(1,await check.VerificationAttempts.CountAsync());
    }

    [Fact]
    public async Task DatabaseFailureNeverAcknowledgesOrAdvancesMission()
    {
        using var f=new Fixture();
        var report=f.Report(0);
        using(var broken=f.OpenDb(new FailSave()))
            await Assert.ThrowsAsync<IOException>(()=>new MissionExecutionService(broken,f.Routes,f.Config).ProgressAsync("R01",report,default));
        using(var check=f.OpenDb())
        {
            Assert.Equal(0,await check.RobotQrScanEvents.CountAsync());
            Assert.Equal(0,(await check.MissionExecutions.SingleAsync()).NextWaypointIndex);
            Assert.Equal(0,await check.MissionWaypoints.CountAsync(w=>w.IsReached));
        }
        Assert.True((await f.Service().ProgressAsync("R01",report,default)).Accepted);
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default) => throw new IOException("Injected storage outage");
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get;set; }="Tests"; public string EnvironmentName { get;set; }="Testing";
        public string ContentRootPath { get;set; }=""; public IFileProvider ContentRootFileProvider { get;set; }=new NullFileProvider();
        public string WebRootPath { get;set; }=""; public IFileProvider WebRootFileProvider { get;set; }=new NullFileProvider();
    }
}
