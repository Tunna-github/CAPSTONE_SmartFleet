using System.Security.Claims;
using SmartFleetBE.Hubs;
using SmartFleetBE.Navigation;
using SmartFleetBE.Services;

namespace SmartFleetBE.Tests;

public sealed class SimulationTests
{
    private readonly WarehouseMapDocument map = new() { Revision=7,Rows=18,Columns=28 };
    private static RobotPose Pose()=>new("R01","run",1,7,"SIMULATION_GROUND_TRUTH","GRID_COL_ROW",4,5,0,DateTime.UtcNow);

    [Fact]
    public void OnlyRegisteredRunAndIncreasingSequenceAccepted()
    {
        var store=new RobotPoseStore();var pose=Pose();
        Assert.False(store.TryAccept(pose,map));
        store.BeginRun("run");Assert.True(store.TryAccept(pose,map));
        Assert.False(store.TryAccept(pose,map));
        Assert.True(store.TryAccept(pose with {Sequence=2},map));
        store.BeginRun("new");Assert.Empty(store.Snapshot(7));
        Assert.False(store.TryAccept(pose with {Sequence=3},map));
        Assert.True(store.TryAccept(pose with {RunId="new"},map));
        Assert.Empty(store.Snapshot(8));
    }

    [Fact]
    public void InvalidStaleOrMismatchedPoseNeverOverwritesPosition()
    {
        var store=new RobotPoseStore();store.BeginRun("run");var p=Pose();
        foreach(var invalid in new[] {p with {RunId=null!},p with {RobotCode=null!},p with {Sequence=-1},
            p with {MapRevision=6},p with {Source="ODOMETRY"},p with {Frame="WORLD"},p with {X=double.NaN},
            p with {Y=double.PositiveInfinity},p with {Yaw=181},p with {X=28},p with {Y=-1},
            p with {CapturedAtUtc=DateTime.UtcNow.AddSeconds(-10)},p with {CapturedAtUtc=DateTime.UtcNow.AddSeconds(10)}})
            Assert.False(store.TryAccept(invalid,map));
        Assert.Empty(store.Snapshot(7));
    }

    [Fact]
    public void PoseTimeIsComparedAsAnInstantAcrossServerTimezones()
    {
        var store=new RobotPoseStore();store.BeginRun("run");
        Assert.True(store.TryAccept(Pose() with {CapturedAtUtc=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7))},map));
    }

    [Theory]
    [InlineData("w3d;broadcast",false,true)]
    [InlineData("w3d;broadcast",true,false)]
    [InlineData("w3d",false,false)]
    [InlineData("reset",false,false)]
    [InlineData("pause",true,false)]
    [InlineData("real-time:100",true,false)]
    [InlineData("robot:R01:move",true,false)]
    public void ViewerCannotControlSimulation(string message,bool initialized,bool allowed)=>
        Assert.Equal(allowed,SimulationViewerSessions.IsAllowedClientMessage(message,initialized));

    [Fact]
    public void ViewerSessionExpiresWithJwtAndIsRevokedWithAccount()
    {
        var connections=new FleetConnectionRegistry();var sessions=new SimulationViewerSessions(connections);
        var user=new ClaimsPrincipal(new ClaimsIdentity(new[] {new Claim(ClaimTypes.NameIdentifier,"42"),
            new Claim("exp",DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds().ToString())},"test"));
        var session=sessions.Create(user);
        Assert.NotNull(sessions.Get(session.Id));
        Assert.True(session.ExpiresAtUtc<DateTime.UtcNow.AddMinutes(2));
        Assert.Null(sessions.Get("bad"));connections.RevokeUser(42);Assert.Null(sessions.Get(session.Id));
    }

    [Fact]
    public void SavedMapMustMatchCommissionedGeometryAndTrafficLayout()
    {
        var scene=new SimulationScene {
            Anchors=Enumerable.Range(0,3).Select(i=>new SimulationScene.Anchor(i,0,i,90,.04)).ToList(),
            Corridors=[[0,1],[1,2]], Intersections=[1],
        };
        var grid=new WarehouseMapDocument {Cells=[Enumerable.Range(0,3).Select(i=>new WarehouseMapCell {
            Type="qr",Label=$"QR{i}",ArucoId=i,MarkerYawDeg=90,MarkerSizeM=.04,IsIntersection=i==1}).ToList()],
            Links=[["QR1","QR0"],["QR1","QR2"],["S1","S2"]]};
        Assert.True(scene.Matches(grid));
        grid.Cells[0][2].DisplayName="Waiting Area A";
        grid.Cells[0][2].OperationalRole="WAITING";
        Assert.True(scene.Matches(grid));
        grid.Links.Add(["QR0","QR2"]);
        Assert.False(scene.Matches(grid));
        grid.Links.RemoveAt(3);
        grid.Cells[0][1].IsIntersection=false;
        Assert.False(scene.Matches(grid));
        grid.Cells[0][1].IsIntersection=true;
        grid.Cells[0][1].MarkerYawDeg=0;
        Assert.False(scene.Matches(grid));
    }

    [Fact]
    public void AssetGatewayOnlyServesCommissionedFiles()
    {
        var scene=new SimulationScene {Assets=new() {["textures/QR_1.png"]="hash"}};
        Assert.True(scene.AllowsAsset("textures/QR_1.png"));
        foreach(var path in new[] {"../config/scene-manifest.json","%2e%2e/file.png","controllers/robot.py","~WEBOTS_HOME/file","textures/missing.png"})
            Assert.False(scene.AllowsAsset(path));
    }
}
