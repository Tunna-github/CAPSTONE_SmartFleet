using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed class SimulationScene
{
    public sealed record Anchor(int ArucoId,int Row,int Col,double MarkerYawDeg,double MarkerSizeM);
    public string SceneId { get; set; } = "";
    public int WarehouseId { get; set; }
    public string WorldSha256 { get; set; } = "";
    public List<Anchor> Anchors { get; set; } = [];
    public List<int[]> Corridors { get; set; } = [];
    public List<int> Intersections { get; set; } = [];
    public Dictionary<string,string> Assets { get; set; } = [];
    public bool AllowsAsset(string asset) => Assets.ContainsKey(asset);
    public static SimulationScene Load(IConfiguration config, IWebHostEnvironment env)
    {
        if (!config.GetValue<bool>("Simulation:Enabled")) return new();
        var path=config["Simulation:ManifestPath"] ?? Path.Combine(env.ContentRootPath,"..","simulation","config","scene-manifest.json");
        return JsonSerializer.Deserialize<SimulationScene>(File.ReadAllText(path),new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Simulation manifest is missing.");
    }
    public bool Matches(WarehouseMapDocument map)
    {
        var cells=map.Cells.SelectMany((row,r)=>row.Select((cell,c)=>new {cell,r,c})).Where(p=>p.cell.Type=="qr").ToList();
        var geometryMatches = Anchors.Count>=3 && cells.Count==Anchors.Count && Anchors.All(a=>cells.Any(p=>
            p.cell.ArucoId==a.ArucoId && p.r==a.Row && p.c==a.Col && p.cell.MarkerYawDeg.HasValue &&
            Math.Abs(p.cell.MarkerYawDeg.Value-a.MarkerYawDeg)<.01 && p.cell.MarkerSizeM.HasValue &&
            Math.Abs(p.cell.MarkerSizeM.Value-a.MarkerSizeM)<.00001));
        if (!geometryMatches || Corridors.Count == 0 || Corridors.Any(edge => edge.Length != 2))
            return false;
        if (!Intersections.ToHashSet().SetEquals(cells.Where(p => p.cell.IsIntersection).Select(p => p.cell.ArucoId!.Value)))
            return false;

        var idsByLabel = cells.Where(p => p.cell.Label != null)
            .ToDictionary(p => p.cell.Label!, p => p.cell.ArucoId!.Value, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<(int, int)>();
        foreach (var edge in map.Links)
        {
            if (idsByLabel.TryGetValue(edge[0], out var from) && idsByLabel.TryGetValue(edge[1], out var to))
                actual.Add(OrderedCorridor(from, to));
        }
        return actual.SetEquals(Corridors.Select(edge => OrderedCorridor(edge[0], edge[1])));
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
    public IReadOnlyList<RobotPose> Snapshot(int revision)
    {
        lock(gate) return poses.Values.Where(p=>p.MapRevision==revision && p.CapturedAtUtc>Now.AddSeconds(-15)).ToList();
    }
}
