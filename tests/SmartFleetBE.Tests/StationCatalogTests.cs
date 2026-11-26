using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;
using SmartFleetBE.Repositories;
using SmartFleetBE.Services;
using SmartFleetBE.Services.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using SmartFleetBE.Hubs;

namespace SmartFleetBE.Tests;

public sealed class StationCatalogTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly SmartFleetDbContext Db;
        public readonly NavigationCoordinator Nav;
        public readonly QrDemoRouteService Routes;
        public readonly StationCatalogService Catalog;
        public readonly IConfiguration Config;
        private readonly Env env = new();
        public Fixture()
        {
            Db = new(new DbContextOptionsBuilder<SmartFleetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(), o => o.EnableNullChecks(false))
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["DemoRoute:Enabled"]="true" }).Build();
            Nav = new(env,Config,NullLogger<NavigationCoordinator>.Instance,new MapStore());
            Routes = new(Nav,env,Config,NullLogger<QrDemoRouteService>.Instance);
            Catalog = new(Db,Nav);
            Db.Warehouses.Add(new() { WarehouseId=1,WarehouseCode="W1",WarehouseName="Warehouse",IsActive=true });
            Db.Zones.Add(new() {ZoneId=1,WarehouseId=1,ZoneCode="Z1",ZoneName="Floor",ZoneType="TRANSIT",IsActive=true});
            Db.Stations.AddRange(new Station {StationId=1,ZoneId=1,StationCode="P1",StationName="Pickup",StationType="PICKUP",QrCodePayload="P1",IsActive=true},
                new Station {StationId=2,ZoneId=1,StationCode="D1",StationName="Delivery",StationType="DELIVERY",QrCodePayload="D1",IsActive=true});
            Db.Robots.AddRange(new Robot {RobotId=1,WarehouseId=1,RobotCode="R01",IsActive=true}, new Robot {RobotId=2,WarehouseId=1,RobotCode="R02",IsActive=true});
            Db.SaveChanges();
        }
        public StationWrite Request(string type="PICKUP",int? qr=2,int revision=1,string code="P1")=>new(1,code,code+" display",type,1,2,0,0,type=="PICKUP"?"P1":type=="DELIVERY"?"D1":null,qr,true,1,revision);
        public void Hold() {
            Db.TransportTasks.Add(new() {TaskId=1,WarehouseId=1,PickupStationId=1,DeliveryStationId=2,TaskStatus="FAILED"});
            Db.TaskAssignments.Add(new() {AssignmentId=1,TaskId=1,RobotId=1,AssignmentStatus="ACTIVE"}); Db.SaveChanges();
        }
        public void Dispose() {Db.Dispose(); if(Directory.Exists(env.ContentRootPath))Directory.Delete(env.ContentRootPath,true);}
    }
    private sealed class MapStore : INavigationMapStore
    {
        public int? SelectedWarehouseId=>1;
        public WarehouseMapDocument LoadOrCreate(WarehouseMapDocument _) {
            var map=new WarehouseMapDocument {Rows=18,Columns=28,Cells=Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,28).Select(_=>new WarehouseMapCell {Type="floor"}).ToList()).ToList()};
            for(int i=0;i<9;i++){map.Cells[2+i/3][2+i%3]=new(){Type="qr",Label=$"QR{i}",ArucoId=i,MarkerSizeM=.04,MarkerYawDeg=0,IsIntersection=i==4};if(i%3>0)map.Links.Add([$"QR{i-1}",$"QR{i}"]);if(i>=3)map.Links.Add([$"QR{i-3}",$"QR{i}"]);}return map;
        }
        public void Save(WarehouseMapDocument m,int r) { }
    }
    private sealed class Env : IWebHostEnvironment {
        public string ContentRootPath{get;set;}=Path.Combine(Path.GetTempPath(),"station-tests-"+Guid.NewGuid());
        public string EnvironmentName{get;set;}="Development";public string ApplicationName{get;set;}="Test";public string WebRootPath{get;set;}="";
        public IFileProvider ContentRootFileProvider{get;set;}=new NullFileProvider();public IFileProvider WebRootFileProvider{get;set;}=new NullFileProvider();
    }

    [Fact] public async Task StationIdsResolveToCommissionedQrWithoutChangingPhysicalMap() {
        using var f=new Fixture();var before=JsonSerializer.Serialize(f.Nav.GetMap());
        await f.Catalog.SaveStation(1,f.Request(),1,default);
        await f.Catalog.SaveStation(2,f.Request("DELIVERY",7,1,"D1"),1,default);
        var resolved=await TaskLocationPolicy.ResolveRouteAsync(f.Db,1,1,2,f.Routes,default);
        Assert.Equal(2,resolved.PickupArUcoId);Assert.Equal(7,resolved.DeliveryArUcoId);
        f.Routes.ConfigureTask(99,resolved.PickupArUcoId,resolved.DeliveryArUcoId);
        Assert.True(f.Routes.ReceiveProgress("R01",new(){Event="READY",ArucoId=0,MapRevision=f.Nav.GetMap().Revision,HeadingDeg=0,LocalizationSource="ARUCO_IMU",ReprojectionErrorPx=.1,StableFrames=5}).Accepted);
        using var command=JsonDocument.Parse(f.Routes.BuildMqttCommand(new(){AssignmentId=99,TaskId=99,Robot=new(){RobotCode="R01",WarehouseId=1},Mission=new(){MissionId=99}}));
        Assert.Equal(2,command.RootElement.GetProperty("pickupArUcoId").GetInt32());Assert.Equal(7,command.RootElement.GetProperty("deliveryArUcoId").GetInt32());
        Assert.Equal(before,JsonSerializer.Serialize(f.Nav.GetMap()));Assert.Equal(2,f.Db.SystemAuditLogs.Count());
    }

    [Theory][InlineData(4)][InlineData(49)][InlineData(-1)]
    public async Task InvalidMissingOrIntersectionMarkerIsRejected(int qr) {using var f=new Fixture();await Assert.ThrowsAsync<ArgumentException>(()=>f.Catalog.SaveStation(1,f.Request(qr:qr),1,default));Assert.Empty(f.Db.StationCheckpoints);}

    [Fact] public async Task MarkerCannotMapToTwoStationsIncludingInactiveOnes() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request(),1,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.SaveStation(2,f.Request("DELIVERY",2,1,"D1"),1,default));
    }

    [Fact] public async Task FailedAssignmentStillBlocksCatalogAndParentChanges() {
        using var f=new Fixture();f.Hold();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.SaveStation(1,f.Request(),1,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.SaveZone(1,new(1,"Z1","Floor","TRANSIT",.18m,true,1),1,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.SaveWarehouse(1,new("W1","New name",null,true,1),1,default));
    }

    [Fact] public async Task StaleRevisionCannotOverwriteAnotherAdmin() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request(),1,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.SaveStation(1,f.Request(),1,default));
    }

    [Fact] public async Task WaitingIsNotPickupAndDualUseIsRejected() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request("WAITING"),1,default);
        await f.Catalog.SaveStation(2,f.Request("DELIVERY",7,1,"D1"),1,default);
        await Assert.ThrowsAsync<ArgumentException>(()=>TaskLocationPolicy.ResolveRouteAsync(f.Db,1,1,2,f.Routes,default));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Catalog.SaveStation(null,f.Request("PICKUP_DELIVERY",0,0,"DUAL"),1,default));
    }

    [Fact] public async Task BerthCapacityIsOneAndReleaseNeedsPhysicalConfirmation() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request("CHARGING"),1,default);
        await f.Catalog.Berth(1,new(1,false,false,"Reserve charge berth"),1,default);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.Berth(1,new(2,false,false,"Second robot"),1,default));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.Berth(1,new(1,true,false,"No clearance"),1,default));
        await f.Catalog.Berth(1,new(1,true,true,"Physically clear"),1,default);Assert.Empty(f.Db.StationBerths);
    }

    [Fact] public async Task OfflineCameraOccupancyIsNotErasedByBerthRelease() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request("WAITING"),1,default);
        await f.Catalog.Berth(1,new(1,false,false,"Reserve waiting berth"),1,default);f.Nav.RecordCameraCheckpoint("R01",2);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Catalog.Berth(1,new(1,true,true,"Still physically there"),1,default));
        f.Nav.RecordCameraCheckpoint("R01",0);await f.Catalog.Berth(1,new(1,true,true,"Moved and clear"),1,default);Assert.Empty(f.Db.StationBerths);
    }

    [Fact] public async Task QueuedTaskResolvesNewMappingAtDispatchAndMissingMappingFailsClosed() {
        using var f=new Fixture();await f.Catalog.SaveStation(1,f.Request(),1,default);await f.Catalog.SaveStation(2,f.Request("DELIVERY",7,1,"D1"),1,default);
        f.Db.TransportTasks.Add(new(){TaskId=1,WarehouseId=1,PickupStationId=1,DeliveryStationId=2,TaskStatus="QUEUED"});await f.Db.SaveChangesAsync();
        Assert.Null(await TaskLocationPolicy.SynchronizeRouteAsync(f.Db,1,f.Routes,default));
        await f.Catalog.SaveStation(1,f.Request(qr:0,revision:2),1,default);
        Assert.Null(await TaskLocationPolicy.SynchronizeRouteAsync(f.Db,1,f.Routes,default));Assert.Equal(0,f.Routes.GetTaskSelection(1)!.PickupArUcoId);
        var station=await f.Db.Stations.SingleAsync(s=>s.StationId==1);station.IsActive=false;await f.Db.SaveChangesAsync();
        Assert.NotNull(await TaskLocationPolicy.SynchronizeRouteAsync(f.Db,1,f.Routes,default));
    }
}
