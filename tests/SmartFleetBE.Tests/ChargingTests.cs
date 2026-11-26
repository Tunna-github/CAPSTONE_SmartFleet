using System.Text.Json;
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

public sealed class ChargingTests
{
    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName {get;set;}="Test"; public string EnvironmentName {get;set;}="Test";
        public string ContentRootPath {get;set;}=Path.Combine(Path.GetTempPath(),"charging-"+Guid.NewGuid().ToString("N"));
        public IFileProvider ContentRootFileProvider {get;set;}=new NullFileProvider();
        public string WebRootPath {get;set;}=""; public IFileProvider WebRootFileProvider {get;set;}=new NullFileProvider();
    }
    private sealed class Fixture : IDisposable
    {
        public readonly SmartFleetDbContext Db;
        public readonly NavigationCoordinator Nav;
        public readonly QrDemoRouteService Routes;
        public readonly ChargingService Charging;
        public readonly WarehouseMapDocument Map;
        public readonly IConfiguration Config;
        private readonly Env env=new();
        public Fixture()
        {
            var root=Directory.GetCurrentDirectory();
            while(!Directory.Exists(Path.Combine(root,"simulation"))) root=Directory.GetParent(root)!.FullName;
            Map=JsonSerializer.Deserialize<WarehouseMapDocument>(File.ReadAllText(Path.Combine(root,"simulation/config/dual-lane-map.json")),ChargingService.Json)!;
            Db=new(new DbContextOptionsBuilder<SmartFleetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(),b=>b.EnableNullChecks(false))
                .ConfigureWarnings(w=>w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            var cfg=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Charging:Enabled"]="true",["Navigation:Enabled"]="true",["DemoRoute:Enabled"]="true",["Mqtt:EnableWorkers"]="true" }).Build();
            Config=cfg;
            Nav=new(env,cfg,NullLogger<NavigationCoordinator>.Instance,new Store(Map));
            Routes=new(Nav,env,cfg,NullLogger<QrDemoRouteService>.Instance);
            Charging=new(Db,Routes,Nav,cfg);
            var w=new Warehouse {WarehouseId=1,WarehouseCode="W",WarehouseName="W",IsActive=true};
            var z=new Zone {ZoneId=1,WarehouseId=1,Warehouse=w,ZoneCode="C",ZoneName="C",IsActive=true};
            Db.Warehouses.Add(w);Db.Zones.Add(z);
            for(int i=1;i<=4;i++) {
                Db.Robots.Add(new Robot {RobotId=i,WarehouseId=1,RobotCode=$"R0{i}",IsActive=true,OperationalStatus="BUSY",ConnectionStatus="ONLINE",BatteryPercent=85,LastHeartbeatTime=DateTime.UtcNow,LastTelemetryTime=DateTime.UtcNow});
                Db.Stations.Add(new Station {StationId=i,ZoneId=1,Zone=z,StationCode=$"CHG{i}",StationName=$"Charging {i}",StationType="CHARGING",IsActive=true,
                    Checkpoint=new() {StationId=i,WarehouseId=1,ArucoId=66+i}});
                Routes.RecordCameraProgress($"R0{i}",new[]{0,4,6,3}[i-1],0,Map.Revision);
            }
            Db.SaveChanges();
        }
        public async Task<ChargingJob> Queue(int robot=1,long mission=1) {
            await ChargingService.QueueAfterDelivery(Db,await Db.Robots.SingleAsync(r=>r.RobotId==robot),mission,default);
            await Db.SaveChangesAsync();return await Db.ChargingJobs.SingleAsync(j=>j.RobotId==robot&&j.State!="COMPLETED");
        }
        public async Task<List<QrDemoRouteService.QrWaypoint>> Start(ChargingJob j) {
            var payload=await Charging.Tick(j.Id,default);Assert.NotNull(payload);
            using var doc=JsonDocument.Parse(payload!);
            return JsonSerializer.Deserialize<List<QrDemoRouteService.QrWaypoint>>(doc.RootElement.GetProperty("route").GetProperty("waypoints"),ChargingService.Json)!;
        }
        public QrDemoRouteService.RouteProgressReport Scan(ChargingJob j,List<QrDemoRouteService.QrWaypoint> p,int index)=>new() {
            Event="CHARGE_WAYPOINT",EventId=Guid.NewGuid().ToString(),ChargingJobId=j.Id,Generation=j.Generation,MapRevision=Map.Revision,
            ArucoId=p[index].ArucoId,WaypointIndex=index,RobotStopped=true,HeadingDeg=0 };
        public void Dispose(){Db.Dispose();if(Directory.Exists(env.ContentRootPath))Directory.Delete(env.ContentRootPath,true);}
    }
    private sealed class Store(WarehouseMapDocument map):INavigationMapStore {
        public int? SelectedWarehouseId=>1;public WarehouseMapDocument LoadOrCreate(WarehouseMapDocument _) =>map;
        public void Save(WarehouseMapDocument m,int revision) { }
    }

    [Fact] public async Task CompletionIsIdempotentAndDoesNotReleaseRobot() {
        using var f=new Fixture();var j=await f.Queue();await f.Queue();
        Assert.Single(await f.Db.ChargingJobs.ToListAsync());Assert.Equal("BUSY",j.Robot.OperationalStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ChargingService.BeginDeparture(f.Db,1,default));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledAndRecoveredEmptyRobotsHandoffToChargingWithoutBecomingAvailable(bool recovered) {
        using var f=new Fixture();var robot=await f.Db.Robots.SingleAsync(r=>r.RobotId==1);
        var task=new TransportTask {TaskId=101,WarehouseId=1,PickupStationId=1,DeliveryStationId=2,TaskStatus=recovered?"QUEUED":"EXECUTING"};
        var assignment=new TaskAssignment {AssignmentId=101,Task=task,Robot=robot,AssignmentStatus=recovered?"FAILED":"ACTIVE"};
        var mission=new Mission {MissionId=101,Assignment=assignment,MissionStatus=recovered?"FAILED":"INITIALIZING",FailureReason=recovered?"Original navigation fault":null};
        f.Db.Missions.Add(mission);
        if(recovered) {
            robot.OperationalStatus="MAINTENANCE";
            f.Db.TaskInterventions.Add(new(){TaskId=101,State="NORMAL",CommandAcknowledged=true});
            f.Db.MissionExecutions.Add(new(){MissionId=101,RoutePayloadJson="{\"pickupWaypointIndex\":5}",NextWaypointIndex=1});
        }
        await f.Db.SaveChangesAsync();var service=new TaskInterventionService(f.Db,f.Routes,f.Config);
        if(recovered) await service.Act(101,"return-to-charge",new("Inspected empty recovered robot",true,true,false,101),42,default);
        else {
            await service.Act(101,"cancel",new("Duplicate order"),42,default);
            var stopped=f.Db.TaskInterventions.Single();
            Assert.Equal(MissionEventResult.Applied,await service.HandleEvent("R01",new(){Event="CONTROL_STOPPED",TaskId=101,AssignmentId=101,MissionId=101,CommandId=stopped.CommandId,CurrentArUcoId=0},default));
        }
        var control=f.Db.TaskInterventions.Single();Assert.Equal("RETURNING_HOME",control.State);
        Assert.Equal(MissionEventResult.Applied,await service.HandleEvent("R01",new(){Event="HOME_REACHED",TaskId=101,AssignmentId=101,MissionId=101,CommandId=control.CommandId,CurrentArUcoId=0},default));
        Assert.Equal("BUSY",robot.OperationalStatus);Assert.Single(f.Db.ChargingJobs);
        Assert.DoesNotContain(f.Db.RobotStatusHistories,h=>h.NewOperationalStatus=="AVAILABLE");
        if(recovered) {Assert.Equal("QUEUED",task.TaskStatus);Assert.Equal("FAILED",assignment.AssignmentStatus);Assert.Equal("FAILED",mission.MissionStatus);Assert.Equal("Original navigation fault",mission.FailureReason);}
        else {Assert.Equal("CANCELLED",task.TaskStatus);Assert.Equal("CANCELLED",assignment.AssignmentStatus);Assert.Equal("ABORTED",mission.MissionStatus);}
        Assert.False(f.Routes.HasFreshLocalization("R01"));
        var job=await f.Db.ChargingJobs.SingleAsync();Assert.Null(await f.Charging.Tick(job.Id,default));
        f.Routes.RecordCameraProgress("R01",0,0,f.Map.Revision);
        await f.Start(job);Assert.Equal("RETURNING",job.State);
    }
    [Fact] public async Task FourReturningRobotsReserveFourDistinctPullThroughBays() {
        using var f=new Fixture();
        for(int i=1;i<=4;i++){var j=await f.Queue(i,i);var path=await f.Start(j);Assert.Equal(66+i,path[^1].ArucoId);Assert.Equal("RETURNING",j.State);}
        Assert.Equal(4,(await f.Db.StationBerths.Select(b=>b.StationId).Distinct().ToListAsync()).Count);
    }
    [Fact] public async Task OnlyOrderedCameraArrivalThenSafetyAndBatteryMakeRobotReady() {
        using var f=new Fixture();var j=await f.Queue();var p=await f.Start(j);
        Assert.False((await f.Charging.Progress("R01",f.Scan(j,p,p.Count-1),default)).Accepted);
        var wrong=f.Scan(j,p,0);wrong.Generation++;Assert.False((await f.Charging.Progress("R01",wrong,default)).Accepted);
        for(int i=0;i<p.Count;i++) Assert.True((await f.Charging.Progress("R01",f.Scan(j,p,i),default)).Accepted);
        await f.Charging.Tick(j.Id,default);Assert.Equal("DOCKED",j.State);
        var docked=f.Scan(j,p,p.Count-1);docked.Event="CHARGE_DOCK_STATUS";
        Assert.True((await f.Charging.Progress("R01",docked,default)).Accepted);
        Assert.Equal("DOCKED",j.State);j.Robot.BatteryPercent=19;await f.Db.SaveChangesAsync();await f.Charging.Tick(j.Id,default);
        Assert.Equal("CHARGING",j.Robot.OperationalStatus);Assert.Equal("DOCKED",j.State);
        j.Robot.BatteryPercent=20;await f.Db.SaveChangesAsync();await f.Charging.Tick(j.Id,default);
        Assert.Equal("READY",j.State);Assert.Equal("AVAILABLE",j.Robot.OperationalStatus);
        await ChargingService.BeginDeparture(f.Db,1,default);await f.Db.SaveChangesAsync();
        await ChargingService.ConfirmDeparture(f.Db,1,p[^1].ArucoId,default);Assert.Single(await f.Db.StationBerths.ToListAsync());
        await ChargingService.ConfirmDeparture(f.Db,1,63,default);await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.StationBerths.ToListAsync());Assert.Equal("COMPLETED",j.State);
    }
    [Fact] public async Task LostRobotKeepsReservedBerthAndCannotBeRedispatched() {
        using var f=new Fixture();var j=await f.Queue();await f.Start(j);
        j.Robot.ConnectionStatus="OFFLINE";await f.Db.SaveChangesAsync();await f.Charging.Tick(j.Id,default);
        Assert.Equal("FAILED",j.State);Assert.Single(await f.Db.StationBerths.ToListAsync());
        Assert.Equal("ERROR",j.Robot.OperationalStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ChargingService.BeginDeparture(f.Db,1,default));
    }
    [Fact] public async Task PausedCycleRequiresPhysicalCheckFreshLocalizationAndNewGeneration() {
        using var f=new Fixture();var j=await f.Queue();await f.Start(j);int gen=j.Generation;
        await f.Charging.Control(j.Id,false,1,"Safety stop for test",false,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Charging.Control(j.Id,true,1,"Area inspected",false,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Charging.Control(j.Id,true,1,"Stopped robot inspected",true,default));
        Assert.True((await f.Charging.Progress("R01",new() {Event="CHARGE_STOPPED",EventId=Guid.NewGuid().ToString(),ChargingJobId=j.Id,Generation=j.Generation,RobotStopped=true},default)).Accepted);
        await f.Charging.Control(j.Id,true,1,"Stopped robot inspected",true,default);
        Assert.Equal(gen+1,j.Generation);Assert.Equal("WAITING",j.State);Assert.Single(await f.Db.StationBerths.ToListAsync());
    }
    [Fact] public void SceneRejectsReverseLaneAndChangedBayRole() {
        using var f=new Fixture();
        var scene=new SimulationScene {Anchors=f.Map.Cells.SelectMany((row,r)=>row.Select((c,col)=>(c,r,col))).Where(p=>p.c.Type=="qr")
            .Select(p=>new SimulationScene.Anchor(p.c.ArucoId!.Value,p.r,p.col,p.c.MarkerYawDeg!.Value,p.c.MarkerSizeM!.Value,p.c.OperationalRole)).ToList(),
            DirectedLinks=true,MarkerDictionary="DICT_4X4_250",Intersections=f.Map.Cells.SelectMany(r=>r).Where(c=>c.IsIntersection).Select(c=>c.ArucoId!.Value).ToList(),Corridors=f.Map.Links.Select(l=>l.Select(s=>int.Parse(s[2..])).ToArray()).ToList()};
        Assert.True(scene.Matches(f.Map));f.Map.Links[0].Reverse();Assert.False(scene.Matches(f.Map));
        f.Map.Links[0].Reverse();f.Map.Cells[3][4].OperationalRole="NAVIGATION";Assert.False(scene.Matches(f.Map));
    }
    [Fact] public void TrafficRejectsDrivingAgainstOrderedLane() {
        using var f=new Fixture();var ids=f.Map.Links[0].Select(s=>int.Parse(s[2..])).ToArray();
        var result=f.Nav.OnScan(1,"R01",new() {CurrentArUcoId=ids[1],NextArUcoId=ids[0]});
        Assert.False(result.Success);
    }
    [Fact] public async Task QrOneIsAnOffRoadTransferBayInTheSeparatedLaneScene() {
        using var f=new Fixture();var cell=f.Map.Cells.SelectMany(r=>r).Single(c=>c.ArucoId==1);
        Assert.False(cell.IsIntersection);Assert.Equal("TRANSFER",cell.OperationalRole);
        Assert.Equal("Instant Noodles",cell.DisplayName);
        f.Routes.ValidateTaskSelection(1,2);f.Routes.ValidateTaskSelection(2,1);
        foreach(var (start,end) in new[]{(1,2),(2,1)}) {
            using var route=JsonDocument.Parse(JsonSerializer.Serialize(f.Routes.BuildHomeRoute(start,end),ChargingService.Json));
            var waypoints=route.RootElement.GetProperty("waypoints");
            Assert.Equal(start,waypoints[0].GetProperty("arucoId").GetInt32());
            Assert.Equal(end,waypoints[waypoints.GetArrayLength()-1].GetProperty("arucoId").GetInt32());
            Assert.False(waypoints[0].GetProperty("isIntersection").GetBoolean());
            Assert.False(waypoints[waypoints.GetArrayLength()-1].GetProperty("isIntersection").GetBoolean());
        }
        Assert.Equal(2,f.Map.Links.Count(e=>e.Contains("QR1")));
        Assert.Contains(f.Map.Links,e=>e.SequenceEqual(new[]{"QR1","QR30"}));
        Assert.Contains(f.Map.Links,e=>e.SequenceEqual(new[]{"QR30","QR1"}));
        var zone=await f.Db.Zones.SingleAsync(z=>z.ZoneId==1);
        foreach(var qr in new[]{1,2}) f.Db.Stations.Add(new Station {StationId=10+qr,ZoneId=1,Zone=zone,
            StationCode=$"QR{qr}",StationName=qr==1?"Instant Noodles":"Snacks & Sweets",StationType="TRANSFER",IsActive=true,
            QrCodePayload=$"QR{qr}",Checkpoint=new(){StationId=10+qr,WarehouseId=1,ArucoId=qr}});
        await f.Db.SaveChangesAsync();
        Assert.Equal(new TaskLocationPolicy.StationRoute(1,2),await TaskLocationPolicy.ResolveRouteAsync(f.Db,1,11,12,f.Routes,default));
        Assert.Equal(new TaskLocationPolicy.StationRoute(2,1),await TaskLocationPolicy.ResolveRouteAsync(f.Db,1,12,11,f.Routes,default));
        // A real intersection designation still makes the same endpoint invalid.
        cell.IsIntersection=true;
        Assert.Throws<ArgumentException>(()=>f.Routes.ValidateTaskSelection(1,2));
    }
    [Fact] public void TwoRobotsCanAdvanceAroundSameRingWithoutArtificialTwoHopDeadlock() {
        using var f=new Fixture();f.Nav.RecordCameraCheckpoint("R01",43);f.Nav.RecordCameraCheckpoint("R02",40);
        f.Nav.OnLidar(1,"R01","CLEAR");f.Nav.OnLidar(2,"R02","CLEAR");
        Assert.Equal("GO",f.Nav.OnScan(1,"R01",new() {CurrentArUcoId=43,NextArUcoId=39,FollowingArUcoId=40}).Directives.Single().Mode);
        Assert.Equal("GO",f.Nav.OnScan(2,"R02",new() {CurrentArUcoId=40,NextArUcoId=41,FollowingArUcoId=42}).Directives.Single().Mode);
        f.Nav.RecordCameraCheckpoint("R02",41);
        Assert.Equal("GO",f.Nav.OnScan(2,"R02",new() {CurrentArUcoId=41,NextArUcoId=42,FollowingArUcoId=43}).Directives.Single().Mode);
        f.Nav.RecordCameraCheckpoint("R01",39);
        Assert.Equal("GO",f.Nav.OnScan(1,"R01",new() {CurrentArUcoId=39,NextArUcoId=40,FollowingArUcoId=41}).Directives.Single().Mode);
    }
    [Fact] public void OccupiedBayKeepsIncomingRobotOutsideMergeSoDockedRobotCanLeave() {
        using var f=new Fixture();f.Nav.RecordCameraCheckpoint("R01",39);f.Nav.RecordCameraCheckpoint("R02",5);
        f.Nav.OnLidar(1,"R01","CLEAR");f.Nav.OnLidar(2,"R02","CLEAR");
        var incoming=f.Nav.OnScan(1,"R01",new() {CurrentArUcoId=39,NextArUcoId=40,FollowingArUcoId=5}).Directives.Single();
        Assert.Equal("STOP",incoming.Mode);Assert.Equal("KEEP_BAY_ENTRY_CLEAR",incoming.Reason);
        Assert.Equal("GO",f.Nav.OnScan(2,"R02",new() {CurrentArUcoId=5,NextArUcoId=40,FollowingArUcoId=41}).Directives.Single().Mode);
    }
    [Fact] public async Task InterruptedDepartureReleasesOnlyVacatedBayAndKeepsMaintenance() {
        using var f=new Fixture();var j=await f.Queue();await f.Start(j);
        j.State="DEPARTING";j.Robot.OperationalStatus="MAINTENANCE";await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Charging.ReleaseRecoveredDeparture(j.Id,1,"Inspected area",false,default));
        f.Routes.RecordCameraProgress("R01",67,0,f.Map.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Charging.ReleaseRecoveredDeparture(j.Id,1,"Inspected area",true,default));
        f.Routes.RecordCameraProgress("R01",63,0,f.Map.Revision);
        await f.Charging.ReleaseRecoveredDeparture(j.Id,1,"Load recovered; camera confirms clear bay",true,default);
        Assert.Equal("COMPLETED",j.State);Assert.Equal("MAINTENANCE",j.Robot.OperationalStatus);Assert.Empty(await f.Db.StationBerths.ToListAsync());
    }
}
