using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed class SimulationScene
{
    public sealed record Anchor(int ArucoId,int Row,int Col,double MarkerYawDeg,double MarkerSizeM,string? OperationalRole=null);
    public string SceneId { get; set; } = "";
    public int WarehouseId { get; set; }
    public string WorldSha256 { get; set; } = "";
    public List<Anchor> Anchors { get; set; } = [];
    public List<int[]> Corridors { get; set; } = [];
    public bool DirectedLinks { get; set; }
    public string MarkerDictionary { get; set; } = "DICT_4X4_50";
    public List<int> Intersections { get; set; } = [];
    public Dictionary<string,string> Assets { get; set; } = [];
    public bool AllowsAsset(string asset) => Assets.ContainsKey(asset);
    public WarehouseMapDocument MapTemplate(WarehouseMapDocument current)
    {
        var result = new WarehouseMapDocument {Revision=current.Revision,DirectedLinks=DirectedLinks,MarkerDictionary=MarkerDictionary,
            Cells=Enumerable.Range(0,18).Select(_=>Enumerable.Range(0,28).Select(_=>new WarehouseMapCell {Type="floor"}).ToList()).ToList(),
            Links=Corridors.Select(e=>new List<string> {$"QR{e[0]}",$"QR{e[1]}"}).ToList()};
        foreach(var a in Anchors) {
            var old=current.Cells.SelectMany(row=>row).FirstOrDefault(c=>c.ArucoId==a.ArucoId);
            result.Cells[a.Row][a.Col]=new() {Type="qr",Label=$"QR{a.ArucoId}",ArucoId=a.ArucoId,MarkerYawDeg=a.MarkerYawDeg,MarkerSizeM=a.MarkerSizeM,
                IsIntersection=Intersections.Contains(a.ArucoId),OperationalRole=a.OperationalRole??old?.OperationalRole,DisplayName=old?.DisplayName};
        }
        return result;
    }
    public static SimulationScene Load(IConfiguration config, IWebHostEnvironment env)
    {
        if (!config.GetValue<bool>("Simulation:Enabled")) return new();
        var path=config["Simulation:ManifestPath"] ?? Path.Combine(env.ContentRootPath,"..","simulation","config","scene-manifest.json");
        var scene = JsonSerializer.Deserialize<SimulationScene>(File.ReadAllText(path),new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Simulation manifest is missing.");
        // Warehouse identity is a deployment binding; geometry/hash/anchors stay
        // exactly as commissioned in the manifest. SharedDatabaseLease validates
        // this configured warehouse against the selected profile before startup.
        if (config.GetValue<bool>("SharedDatabase:Enabled") && config.GetValue<int>("Navigation:WarehouseId") is > 0 and var warehouse)
            scene.WarehouseId = warehouse;
        return scene;
    }
    public bool Matches(WarehouseMapDocument map)
    {
        var cells=map.Cells.SelectMany((row,r)=>row.Select((cell,c)=>new {cell,r,c})).Where(p=>p.cell.Type=="qr").ToList();
        var geometryMatches = Anchors.Count>=3 && cells.Count==Anchors.Count && Anchors.All(a=>cells.Any(p=>
            p.cell.ArucoId==a.ArucoId && p.r==a.Row && p.c==a.Col && p.cell.MarkerYawDeg.HasValue &&
            Math.Abs(p.cell.MarkerYawDeg.Value-a.MarkerYawDeg)<.01 && p.cell.MarkerSizeM.HasValue &&
            Math.Abs(p.cell.MarkerSizeM.Value-a.MarkerSizeM)<.00001 && (a.OperationalRole == null || p.cell.OperationalRole == a.OperationalRole)));
        if (!geometryMatches || map.DirectedLinks != DirectedLinks || map.MarkerDictionary != MarkerDictionary || Corridors.Count == 0 || Corridors.Any(edge => edge.Length != 2))
            return false;
        if (!Intersections.ToHashSet().SetEquals(cells.Where(p => p.cell.IsIntersection).Select(p => p.cell.ArucoId!.Value)))
            return false;

        var idsByLabel = cells.Where(p => p.cell.Label != null)
            .ToDictionary(p => p.cell.Label!, p => p.cell.ArucoId!.Value, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<(int, int)>();
        foreach (var edge in map.Links)
        {
            if (idsByLabel.TryGetValue(edge[0], out var from) && idsByLabel.TryGetValue(edge[1], out var to))
                actual.Add(DirectedLinks ? (from, to) : OrderedCorridor(from, to));
        }
        return actual.SetEquals(Corridors.Select(edge => DirectedLinks ? (edge[0], edge[1]) : OrderedCorridor(edge[0], edge[1])));
    }

    private static (int, int) OrderedCorridor(int from, int to) =>
        (Math.Min(from, to), Math.Max(from, to));
    public static bool ValidBridgeKey(IConfiguration config,string supplied)
    {
        var expected=config["Simulation:BridgeKey"]??"";
        return expected.Length>=32 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }
}

public sealed record RobotPose(string RobotCode,string RunId,long Sequence,int MapRevision,
    string Source,string Frame,double X,double Y,double Yaw,DateTimeOffset CapturedAtUtc);

// Visualization data never changes robot availability, task state, or navigation safety grants.
public sealed class RobotPoseStore(TimeProvider? clock=null)
{
    private readonly object gate=new();
    private readonly Dictionary<string,RobotPose> poses=new(StringComparer.Ordinal);
    private string? runId;
    private readonly HashSet<(string Robot,string Run)> retiredPhysicalRuns=[];
    private DateTimeOffset Now=>(clock??TimeProvider.System).GetUtcNow();
    public void BeginRun(string id)
    {
        lock(gate) if(runId!=id) {runId=id;poses.Clear();}
    }
    public bool TryAccept(RobotPose pose,WarehouseMapDocument map)
    {
        if(string.IsNullOrEmpty(pose.RunId) || pose.RunId.Length>64 || string.IsNullOrEmpty(pose.RobotCode) || pose.Sequence<0 || pose.Source!="SIMULATION_GROUND_TRUTH" || pose.Frame!="GRID_COL_ROW" ||
            pose.MapRevision!=map.Revision || pose.CapturedAtUtc<Now.AddSeconds(-3) || pose.CapturedAtUtc>Now.AddSeconds(3) ||
            !double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Yaw) || pose.Yaw is < -180 or > 180 ||
            pose.X<-.5 || pose.X>map.Columns-.5 || pose.Y<-.5 || pose.Y>map.Rows-.5) return false;
        lock(gate)
        {
            if(runId!=pose.RunId || poses.TryGetValue(pose.RobotCode,out var old) && old.Sequence>=pose.Sequence) return false;
            poses[pose.RobotCode]=pose;return true;
        }
    }
    // Called only after the MQTT worker verifies an active PHYSICAL registry identity.
    // These are estimated visualization poses, never evidence for traffic ownership.
    public bool TryAcceptPhysical(RobotPose pose,WarehouseMapDocument map)
    {
        if(string.IsNullOrEmpty(pose.RunId) || pose.RunId.Length>64 || string.IsNullOrEmpty(pose.RobotCode) ||
            pose.Sequence<0 || pose.Source!="ARUCO_IMU_ODOMETRY" || pose.Frame!="GRID_COL_ROW" ||
            pose.MapRevision!=map.Revision || pose.CapturedAtUtc<Now.AddSeconds(-3) || pose.CapturedAtUtc>Now.AddSeconds(3) ||
            !double.IsFinite(pose.X) || !double.IsFinite(pose.Y) || !double.IsFinite(pose.Yaw) || pose.Yaw is < -180 or > 180 ||
            pose.X<-.5 || pose.X>map.Columns-.5 || pose.Y<-.5 || pose.Y>map.Rows-.5) return false;
        lock(gate)
        {
            if(retiredPhysicalRuns.Contains((pose.RobotCode,pose.RunId))) return false;
            if(poses.TryGetValue(pose.RobotCode,out var old)) {
                if(old.Source!="ARUCO_IMU_ODOMETRY") return false;
                if(old.RunId==pose.RunId && old.Sequence>=pose.Sequence) return false;
                if(old.RunId!=pose.RunId) {
                    if(pose.CapturedAtUtc<=old.CapturedAtUtc || retiredPhysicalRuns.Count>=1024) return false;
                    retiredPhysicalRuns.Add((old.RobotCode,old.RunId));
                }
            }
            poses[pose.RobotCode]=pose;return true;
        }
    }
    public IReadOnlyList<RobotPose> Snapshot(int revision)
    {
        lock(gate) return poses.Values.Where(p=>p.MapRevision==revision && p.CapturedAtUtc>Now.AddSeconds(-15)).ToList();
    }
}
