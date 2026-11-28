using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class PhysicalDemoTests
{
    private sealed class Env : IWebHostEnvironment, IDisposable {
        public string ContentRootPath {get;set;}=Path.Combine(Path.GetTempPath(),"physical-demo-"+Guid.NewGuid());
        public string EnvironmentName{get;set;}="Test";public string ApplicationName{get;set;}="Test";
        public string WebRootPath{get;set;}="";public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();
        public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
        public void Dispose(){if(Directory.Exists(ContentRootPath))Directory.Delete(ContentRootPath,true);}
    }
    private static IConfiguration Config(bool sim=false,bool mixed=false)=>new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?> { ["Simulation:Enabled"]=sim.ToString(),
            ["Dispatch:AllowMixedRobotTypes"]=mixed.ToString() }).Build();

    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public void ExistingDispatchModesRemainRestrictedUnlessMixedSimulationExplicitlyEnabled(bool simulation,bool mixed)
    {
        var config=Config(simulation,mixed);
        Assert.Single(DispatchRobotTypePolicy.AllowedTypes(config));
        Assert.Equal(simulation?"SIMULATION":"PHYSICAL",DispatchRobotTypePolicy.RequiredType(config));
        Assert.False(DispatchRobotTypePolicy.Allows(config,simulation?"PHYSICAL":"SIMULATION"));
    }
    [Fact] public void ExplicitMixedModeAllowsBothKnownTypesAndRejectsOthers()
    {
        var config=Config(true,true);
        Assert.True(DispatchRobotTypePolicy.Allows(config,"PHYSICAL"));
        Assert.True(DispatchRobotTypePolicy.Allows(config,"SIMULATION"));
        Assert.False(DispatchRobotTypePolicy.Allows(config,"UNKNOWN"));
    }

    [Fact] public void PhysicalVisualizationUsesIndependentSessionsAndDoesNotImpersonateWebots()
    {
        var store=new RobotPoseStore();var map=new WarehouseMapDocument {Revision=9};
        var pose=new RobotPose("R01","physical-session",1,9,"ARUCO_IMU_ODOMETRY","GRID_COL_ROW",4,5,0,DateTimeOffset.UtcNow);
        Assert.False(store.TryAccept(pose,map));
        Assert.True(store.TryAcceptPhysical(pose,map));
        Assert.False(store.TryAcceptPhysical(pose,map));
        Assert.False(store.TryAcceptPhysical(pose with {Source="SIMULATION_GROUND_TRUTH",Sequence=2},map));
        Assert.False(store.TryAcceptPhysical(pose with {X=double.NaN,Sequence=2},map));
        Assert.False(store.TryAcceptPhysical(pose with {MapRevision=8,Sequence=2},map));
        Assert.False(store.TryAcceptPhysical(pose with {CapturedAtUtc=DateTimeOffset.UtcNow.AddSeconds(-4),Sequence=2},map));
        var next=pose with {RunId="new-session",CapturedAtUtc=DateTimeOffset.UtcNow.AddMilliseconds(1)};
        Assert.True(store.TryAcceptPhysical(next,map));
        Assert.False(store.TryAcceptPhysical(pose with {Sequence=50,CapturedAtUtc=DateTimeOffset.UtcNow.AddMilliseconds(2)},map));
        store.BeginRun("webots-run");
        Assert.True(store.TryAccept(next with {RobotCode="R02",RunId="webots-run",Source="SIMULATION_GROUND_TRUTH"},map));
    }

    [Fact] public void ExplicitLineDemoRecordsCheckpointWithoutInventingHeadingOrMetricCalibration()
    {
        using var env=new Env();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["DemoRoute:Enabled"]="true",["PhysicalRobots:Enabled"]="true",
            ["PhysicalRobots:LineDemoRobotCodes:0"]="R01" }).Build();
        var nav=new NavigationCoordinator(env,config,NullLogger<NavigationCoordinator>.Instance);
        var map=nav.GetMap();
        map.Cells=Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,28).Select(_=>new WarehouseMapCell{Type="floor"}).ToList()).ToList();
        map.Cells[2][2]=new(){Type="qr",Label="QR5",ArucoId=5};
        map.Cells[2][6]=new(){Type="qr",Label="QR7",ArucoId=7};
        map.Links=[["QR5","QR7"]];Assert.True(nav.ReplaceMap(map).Success);
        var routes=new QrDemoRouteService(nav,env,config,NullLogger<QrDemoRouteService>.Instance);
        var report=new QrDemoRouteService.RouteProgressReport {Event="READY",ArucoId=5,
            MapRevision=nav.GetMap().Revision,LocalizationSource=PhysicalLineDemoPolicy.Source,StableFrames=2,RobotStopped=true};
        Assert.True(routes.ReceiveProgress("R01",report).Accepted);
        var localization=routes.GetRobotLocalizations()["R01"];
        Assert.Null(localization.HeadingDeg);Assert.Equal(PhysicalLineDemoPolicy.Source,localization.Source);
        routes.ConfigureTask(1,5,7);
        Assert.True(routes.CanDispatchTask(1,"R01",out var reason),reason);
        routes.RecordCameraProgress("R01",7,null,nav.GetMap().Revision);
        Assert.Equal(7,routes.GetRobotPositions()["R01"]);
        Assert.Null(routes.GetRobotLocalizations()["R01"].HeadingDeg);
        Assert.False(routes.ReceiveProgress("R02",report).Accepted);
        report.HeadingDeg=0;Assert.False(routes.ReceiveProgress("R01",report).Accepted);
    }

    [Theory][InlineData(false,false,2,true)][InlineData(true,true,2,true)][InlineData(true,false,1,true)][InlineData(true,false,2,false)]
    public void LineDemoCannotBypassExplicitEnableChargingOrStoppedFrameChecks(bool enabled,bool charging,int frames,bool stopped)
    {
        using var env=new Env();
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["DemoRoute:Enabled"]="true",["PhysicalRobots:Enabled"]=enabled.ToString(),["Charging:Enabled"]=charging.ToString(),
            ["PhysicalRobots:LineDemoRobotCodes:0"]="R01" }).Build();
        var nav=new NavigationCoordinator(env,config,NullLogger<NavigationCoordinator>.Instance);
        var map=nav.GetMap();
        map.Cells[2][2]=new(){Type="qr",Label="QR5",ArucoId=5};
        map.Cells[2][6]=new(){Type="qr",Label="QR7",ArucoId=7};
        map.Links=[["QR5","QR7"]];Assert.True(nav.ReplaceMap(map).Success);
        var routes=new QrDemoRouteService(nav,env,config,NullLogger<QrDemoRouteService>.Instance);
        Assert.False(routes.ReceiveProgress("R01",new(){Event="READY",ArucoId=5,
            MapRevision=nav.GetMap().Revision,LocalizationSource=PhysicalLineDemoPolicy.Source,StableFrames=frames,RobotStopped=stopped}).Accepted);
        Assert.False(routes.HasFreshLocalization("R01"));
    }

    [Theory][InlineData("PHYSICAL")][InlineData("SIMULATION")]
    public async Task PhysicalAndMixedContendersWaitUntilActualOwnerExit(string secondType)
    {
        Assert.True(DispatchRobotTypePolicy.Allows(Config(true,true),secondType));
        using var env=new Env();
        var nav=new NavigationCoordinator(env,Config(),NullLogger<NavigationCoordinator>.Instance);
        var map=nav.GetMap();
        map.Cells=Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,28).Select(_=>new WarehouseMapCell{Type="floor"}).ToList()).ToList();
        int[][] ids=[[8,1,3],[0,6,2],[5,7,4]];
        map.Links=[];
        for(int r=0;r<3;r++)for(int c=0;c<3;c++) {
            var id=ids[r][c];map.Cells[r*4+2][c*4+2]=new(){Type="qr",Label=$"QR{id}",ArucoId=id,MarkerSizeM=.18,MarkerYawDeg=0,IsIntersection=id==6};
            if(r>0)map.Links.Add([$"QR{ids[r-1][c]}",$"QR{id}"]);
            if(c>0)map.Links.Add([$"QR{ids[r][c-1]}",$"QR{id}"]);
        }
        Assert.True(nav.ReplaceMap(map).Success);
        nav.OnLidar(1,"R01","CLEAR");nav.OnLidar(2,"R02","CLEAR");
        Assert.True(nav.OnNavigationEvent(1,"R01",new(){Event="APPROACH",IntersectionArUcoId=6,CurrentArUcoId=0}).Success);
        Assert.True(nav.OnNavigationEvent(2,"R02",new(){Event="APPROACH",IntersectionArUcoId=6,CurrentArUcoId=1}).Success);
        await Task.Delay(850);
        var winner=nav.OnLidar(1,"R01","CLEAR");
        Assert.Contains(winner.Directives,d=>d.RobotCode=="R01" && d.IntersectionGranted);
        Assert.Equal("STOP",Assert.Single(nav.OnLidar(2,"R02","CLEAR").Directives).Mode);
        Assert.False(nav.OnNavigationEvent(2,"R02",new(){Event="ENTER",IntersectionArUcoId=6}).Success);
        Assert.True(nav.OnNavigationEvent(1,"R01",new(){Event="ENTER",IntersectionArUcoId=6}).Success);
        nav.OnLidar(1,"R01","NO_SCAN_DATA");
        Assert.Equal("R01",nav.GetIntersection(6)!.ActiveRobotCode);
        Assert.False(nav.OnScan(1,"R01",new(){CurrentArUcoId=6,NextArUcoId=2}).Success);
        Assert.False(nav.OnNavigationEvent(1,"R01",new(){Event="EXIT",IntersectionArUcoId=6,CurrentArUcoId=4}).Success);
        Assert.True(nav.OnNavigationEvent(1,"R01",new(){Event="EXIT",IntersectionArUcoId=6,CurrentArUcoId=2}).Success);
        Assert.Equal("R02",nav.GetIntersection(6)!.ActiveRobotCode);
        Assert.False(nav.OnNavigationEvent(1,"R01",new(){Event="EXIT",IntersectionArUcoId=6,CurrentArUcoId=2}).Success);
    }
}
