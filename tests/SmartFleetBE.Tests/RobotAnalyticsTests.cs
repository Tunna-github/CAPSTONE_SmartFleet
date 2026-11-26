using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class RobotAnalyticsTests
{
    private static readonly DateTime Now = new(2026,11,26,5,0,0,DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026,11,26);
    private sealed class Env : IWebHostEnvironment {
        public string ContentRootPath {get;set;}=Path.Combine(Path.GetTempPath(),"registry-tests-"+Guid.NewGuid());
        public string EnvironmentName {get;set;}="Development"; public string ApplicationName{get;set;}="Test";public string WebRootPath{get;set;}="";
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }
    private sealed class Fixture : IDisposable {
        public readonly SmartFleetDbContext Db;
        public readonly IConfiguration Config=new ConfigurationBuilder().Build();
        private readonly Env env=new();
        public readonly NavigationCoordinator Navigation;
        public RobotRegistryService Registry=>new(Db,Config,Navigation);
        public AnalyticsService Analytics=>new(Db,Config);
        public Fixture() {
            Db=new(new DbContextOptionsBuilder<SmartFleetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(),o=>o.EnableNullChecks(false))
                .ConfigureWarnings(w=>w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Navigation=new(env,Config,NullLogger<NavigationCoordinator>.Instance);
            Db.Warehouses.AddRange(new Warehouse{WarehouseId=1,WarehouseCode="W1",WarehouseName="Warehouse",IsActive=true},new Warehouse{WarehouseId=2,WarehouseCode="W2",WarehouseName="Other",IsActive=false});
            Db.SaveChanges();
        }
        public void SeedReport() {
            var start=Now.Date.AddHours(-7);
            Db.Robots.AddRange(new Robot {RobotId=1,RobotCode="R01",RobotType="PHYSICAL",MqttClientIdentifier="existing",WarehouseId=1,IsActive=true,ConnectionStatus="ONLINE",OperationalStatus="AVAILABLE",BatteryPercent=85,CreatedAt=start.AddDays(-3),LastHeartbeatTime=Now,LastTelemetryTime=Now},
                new Robot{RobotId=2,RobotCode="R02",WarehouseId=1,IsActive=false,ConnectionStatus="OFFLINE",CreatedAt=Now.AddHours(-3)});
            Db.TransportTasks.AddRange(
                new(){TaskId=1,WarehouseId=1,TaskStatus="COMPLETED",CreatedAt=start.AddHours(1),AssignedAt=start.AddMinutes(70),CompletedAt=Now.AddHours(-1)},
                new(){TaskId=2,WarehouseId=1,TaskStatus="COMPLETED",CreatedAt=start.AddDays(-2),CompletedAt=Now.AddHours(-2)},
                new(){TaskId=3,WarehouseId=1,TaskStatus="FAILED",CreatedAt=Now.Date},
                new(){TaskId=4,WarehouseId=1,TaskStatus="CANCELLED",CreatedAt=start.AddHours(2)},
                new(){TaskId=5,WarehouseId=1,TaskStatus="QUEUED",CreatedAt=Now.Date.AddHours(1)},
                new(){TaskId=6,WarehouseId=1,TaskStatus="QUEUED",CreatedAt=start.AddDays(1)},
                new(){TaskId=7,WarehouseId=1,TaskStatus="QUEUED",CreatedAt=start.AddSeconds(-1)});
            Db.TaskAssignments.AddRange(
                new(){AssignmentId=1,TaskId=1,RobotId=1,AssignmentStatus="COMPLETED",AssignedAt=start.AddMinutes(70),EndedAt=Now.AddHours(-1)},
                new(){AssignmentId=2,TaskId=2,RobotId=1,AssignmentStatus="COMPLETED",AssignedAt=start.AddHours(-1),EndedAt=Now.AddHours(-4)},
                new(){AssignmentId=3,TaskId=3,RobotId=1,AssignmentStatus="ACTIVE",AssignedAt=Now.Date});
            Db.Missions.AddRange(
                new(){MissionId=1,AssignmentId=1,MissionStatus="COMPLETED",CreatedAt=start.AddMinutes(70),StartExecutionTime=Now.AddHours(-2),EndExecutionTime=Now.AddHours(-1)},
                new(){MissionId=2,AssignmentId=2,MissionStatus="COMPLETED",CreatedAt=start.AddDays(-1),StartExecutionTime=start.AddDays(-1),EndExecutionTime=Now.AddHours(-2)},
                new(){MissionId=3,AssignmentId=3,MissionStatus="FAILED",CreatedAt=Now.Date});
            Db.RobotMaintenanceRecords.Add(new(){RobotId=2,MaintenanceStatus="SCHEDULED",CreatedAt=Now.Date});
            Db.RobotIncidents.Add(new(){RobotId=1,IncidentType="OFFLINE",Severity="ERROR",CreatedAt=Now.Date});
            Db.SaveChanges();
        }
        public void Dispose(){Db.Dispose();if(Directory.Exists(env.ContentRootPath))Directory.Delete(env.ContentRootPath,true);}
    }
    private static RobotRegistryWrite Registration(string code="R90",string client="smartfleet-R90")=>new(1,code,"PHYSICAL","JetRacer","ROS_AGENT",null,null,null,client,10,true,0);

    [Fact] public async Task RegisterPersistsIdentityAndAuditWithoutInventingDevicePresence() {
        using var f=new Fixture();var id=await f.Registry.Save(null,Registration(),9,default);
        var r=await f.Db.Robots.FindAsync(id);Assert.Equal("OFFLINE",r!.ConnectionStatus);Assert.Equal("AVAILABLE",r.OperationalStatus);
        Assert.Null(r.BatteryPercent);Assert.Null(r.LastHeartbeatTime);Assert.Null(r.LastTelemetryTime);Assert.Equal(1,r.RegistryRevision);
        var audit=Assert.Single(f.Db.SystemAuditLogs);Assert.Equal(9,audit.UserId);Assert.Equal("REGISTER_ROBOT",audit.ActionName);
    }
    [Fact] public async Task DuplicateRobotAndClientIdentifiersAreCaseInsensitiveAndIncludeInactiveRobots() {
        using var f=new Fixture();await f.Registry.Save(null,Registration() with{IsActive=false},9,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Registry.Save(null,Registration("r90","other"),9,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Registry.Save(null,Registration("R91","SMARTFLEET-r90"),9,default));
    }
    [Theory][InlineData("R/90",1)][InlineData("R+",1)][InlineData("R90",0)][InlineData("R90",-1)]
    public async Task InvalidIdentityOrPayloadIsRejected(string code,int payload) {
        using var f=new Fixture();await Assert.ThrowsAsync<ArgumentException>(()=>f.Registry.Save(null,Registration(code) with {MaxPayloadWeightKg=payload},9,default));Assert.Empty(f.Db.Robots);
    }
    [Fact] public async Task InactiveWarehouseInvalidNetworkingAndSimulationInPhysicalEnvironmentAreRejected() {
        using var f=new Fixture();foreach(var request in new[]{Registration() with{WarehouseId=2},Registration() with{IpAddress="not-an-ip"},Registration() with{MacAddress="xx"},Registration() with{RobotType="SIMULATION"},Registration() with{MaxPayloadWeightKg=.001m}})
            await Assert.ThrowsAsync<ArgumentException>(()=>f.Registry.Save(null,request,9,default));
    }
    [Fact] public async Task ConfigurationRevisionAndImmutableIdentityProtectDeviceHistory() {
        using var f=new Fixture();var id=await f.Registry.Save(null,Registration(),9,default);
        await f.Registry.Save(id,Registration() with{Model="Updated model",RegistryRevision=1},9,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Registry.Save(id,Registration() with{RegistryRevision=1},9,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Registry.Save(id,Registration("RENAMED") with{RegistryRevision=2},9,default));
        Assert.Equal("Updated model",(await f.Db.Robots.FindAsync(id))!.Model);
    }
    [Fact] public async Task FailedActiveHoldCannotBeBypassedByConfigurationOrDeactivation() {
        using var f=new Fixture();f.SeedReport();var request=Registration("R01","existing") with{RegistryRevision=1,IsActive=false};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Registry.Save(1,request,9,default));Assert.True((await f.Db.Robots.FindAsync(1))!.IsActive);
    }
    [Fact] public async Task ReportUsesVietnamDayCohortAndSeparateCompletionTimestampWithoutDoubleCountingAssignments() {
        using var f=new Fixture();f.SeedReport();var r=await f.Analytics.Read(Day,Day,1,null,default,Now);
        Assert.Equal(Now.Date.AddHours(-7),r.Window.StartUtc);Assert.Equal(4,r.Tasks.Created);Assert.Equal(1,r.Tasks.Completed);Assert.Equal(1,r.Tasks.Failed);Assert.Equal(1,r.Tasks.Cancelled);
        Assert.Equal(50d,r.Tasks.SuccessRatePercent);Assert.Equal(2,r.Tasks.CompletedInPeriod);Assert.Equal(2,r.Tasks.CompletionDurationSamples);Assert.Equal(600d,r.Tasks.AverageCreateToAssignmentSeconds);
        Assert.Equal(2,r.Missions.Created);Assert.Equal(1,r.Missions.Completed);Assert.Equal(1,r.Missions.FailedWithActiveHold);Assert.Equal(3600d,r.Missions.AverageExecutionSeconds);
        Assert.Equal(4,Assert.Single(r.Daily).CreatedTasks);Assert.Equal(2,r.Daily[0].CompletedTasks);
        Assert.Equal(43200d,r.RobotUtilization.Single(r=>r.RobotId==1).AssignedSeconds);Assert.Equal(100d,r.RobotUtilization[0].UtilizationPercent);Assert.Equal(80d,r.AssignmentUtilizationPercent);
        Assert.Equal(1,r.Fleet.Online);Assert.Equal(0,r.Fleet.Available);Assert.Equal(1,r.Maintenance.OpenNow);Assert.Equal(1,r.Maintenance.UnresolvedIncidentsNow);
    }
    [Fact] public async Task EmptyReportUsesNullForUnmeasuredRatesAndWarehouseRobotFiltersAreValidated() {
        using var f=new Fixture();var r=await f.Analytics.Read(Day,Day,1,null,default,Now);Assert.Null(r.Tasks.SuccessRatePercent);Assert.Null(r.Tasks.AverageCompletionSeconds);Assert.Null(r.AssignmentUtilizationPercent);
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Analytics.Read(Day,Day,99,null,default,Now));f.SeedReport();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Analytics.Read(Day,Day,2,1,default,Now));
        var robot=await f.Analytics.Read(Day,Day,1,1,default,Now);Assert.Equal(2,robot.Tasks.Created);Assert.Single(robot.RobotUtilization);Assert.Equal(2,robot.Tasks.CompletedInPeriod);
    }
    [Fact] public void InvalidFutureReversedAndExcessiveDateRangesAreRejected() {
        Assert.Throws<ArgumentException>(()=>AnalyticsService.Window(Day.AddDays(1),Day,null,null,Now));
        Assert.Throws<ArgumentException>(()=>AnalyticsService.Window(Day,Day.AddDays(1),null,null,Now));
        Assert.Throws<ArgumentException>(()=>AnalyticsService.Window(Day.AddDays(-366),Day,null,null,Now));
        Assert.Throws<ArgumentException>(()=>AnalyticsService.Window(new(1,1,1),new(1,1,1),null,null,Now));
        Assert.Throws<ArgumentException>(()=>AnalyticsService.Window(null,new(1,1,1),null,null,Now));
    }
    [Fact] public void OverlappingAndNestedIntervalsAreMergedBeforeComputingUtilization() {
        Assert.Equal(5*3600,AnalyticsService.UnionSeconds([(Now,Now.AddHours(2)),(Now.AddHours(1),Now.AddHours(3)),(Now.AddMinutes(90),Now.AddMinutes(100)),(Now.AddHours(4),Now.AddHours(6))]));
    }
    [Fact] public async Task ExportsContainTheSameSnapshotAndUntrustedTextCannotBecomeASpreadsheetFormula() {
        using var f=new Fixture();f.SeedReport();(await f.Db.Robots.FindAsync(1))!.RobotCode="=1+1";await f.Db.SaveChangesAsync();
        var r=await f.Analytics.Read(Day,Day,1,null,default,Now);
        using var file=new MemoryStream(AnalyticsExport.Xlsx(r));using var zip=new ZipArchive(file);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));Assert.NotNull(zip.GetEntry("xl/_rels/workbook.xml.rels"));
        foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".xml"))){using var reader=new StreamReader(entry.Open());var xml=XDocument.Parse(reader.ReadToEnd());Assert.DoesNotContain(xml.Descendants(),e=>e.Name.LocalName=="f");}
        using var sheet=new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());Assert.Contains("=1+1",sheet.ReadToEnd());
        var pdf=Encoding.ASCII.GetString(AnalyticsExport.Pdf(r));Assert.StartsWith("%PDF-1.4",pdf);Assert.Contains("SmartFleet Operational Report",pdf);Assert.Contains("2026-11-26",pdf);Assert.EndsWith("%%EOF\n",pdf);
    }
}
