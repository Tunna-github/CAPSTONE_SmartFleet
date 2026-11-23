using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

// Testing-only route planning. No PostgreSQL schema changes. One Backend process.
// The existing navigation map is the ONE source of QR nodes and connections.
public sealed class QrDemoRouteService
{
    public sealed class TaskQrSelection
    {
        public int PickupArUcoId { get; set; }
        public int DeliveryArUcoId { get; set; }
    }

    public sealed class QrWaypoint
    {
        public int ArucoId { get; init; }
        public int Row { get; init; }
        public int Col { get; init; }
        // Null for the initial checkpoint; N/E/S/W for each following linked road.
        public string? TravelDirection { get; init; }
        public string Stage { get; init; } = "TO_PICKUP";
    }

    public sealed class RouteProgressReport
    {
        public string Event { get; set; } = "";
        public string EventId { get; set; } = "";
        public long MissionId { get; set; }
        public int ArucoId { get; set; }
        public int WaypointIndex { get; set; }
    }

    public sealed class MissionView
    {
        public long TaskId { get; init; }
        public long MissionId { get; init; }
        public int MapRevision { get; init; }
        public string RobotCode { get; init; } = "";
        public int StartArUcoId { get; init; }
        public int PickupArUcoId { get; init; }
        public int DeliveryArUcoId { get; init; }
        public int PickupWaypointIndex { get; init; }
        public int NextWaypointIndex { get; set; }
        public int? LastReachedArUcoId { get; set; }
        public bool PickupReached { get; set; }
        public bool DeliveryReached { get; set; }
        public IReadOnlyList<QrWaypoint> Waypoints { get; init; } = [];
    }

    public sealed class StoreDocument
    {
        public Dictionary<long, TaskQrSelection> TaskRoutes { get; set; } = [];
        public Dictionary<string, int> RobotPositions { get; set; } = [];
    }

    private sealed class Node(int id, string label, int row, int col)
    {
        public int Id { get; } = id;
        public string Label { get; } = label;
        public int Row { get; } = row;
        public int Col { get; } = col;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly NavigationCoordinator _navigation;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QrDemoRouteService> _logger;
    private readonly string _statePath;
    private readonly StoreDocument _store;
    private readonly Dictionary<long, MissionView> _missions = [];
    private readonly Dictionary<long, string> _publishedPayloads = [];
    private readonly HashSet<string> _freshRobotPositions = new(StringComparer.OrdinalIgnoreCase);

    public QrDemoRouteService(
        NavigationCoordinator navigation,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<QrDemoRouteService> logger)
    {
        _navigation = navigation;
        _configuration = configuration;
        _logger = logger;
        _statePath = Path.Combine(environment.ContentRootPath, "App_Data", "qr-demo-state.json");
        _store = File.Exists(_statePath)
            ? JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_statePath), JsonOptions)
                ?? throw new InvalidDataException("qr-demo-state.json was empty")
            : new StoreDocument();
        _store.TaskRoutes ??= [];
        _store.RobotPositions ??= [];
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_store, JsonOptions));
        File.Move(tmp, _statePath, overwrite: true);
    }

    public IReadOnlyDictionary<string, int> GetRobotPositions()
    {
        lock (_gate) return _store.RobotPositions
            .Where(p => _freshRobotPositions.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MissionView> GetMissions()
    {
        lock (_gate) return _missions.Values.OrderByDescending(x => x.MissionId)
            .Select(CloneMission).ToList();
    }

    public MissionView? GetMission(long id)
    {
        lock (_gate) return _missions.TryGetValue(id, out var m) ? CloneMission(m) : null;
    }

    public TaskQrSelection? GetTaskSelection(long taskId)
    {
        lock (_gate) return _store.TaskRoutes.TryGetValue(taskId, out var x)
            ? new TaskQrSelection { PickupArUcoId = x.PickupArUcoId, DeliveryArUcoId = x.DeliveryArUcoId }
            : null;
    }

    public void ConfigureTask(long taskId, int pickupId, int deliveryId)
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        if (!nodes.ContainsKey(pickupId) || !nodes.ContainsKey(deliveryId))
            throw new ArgumentException("Pickup and delivery must be existing QR ArUco IDs in the saved Backend map.");
        if (pickupId == deliveryId)
            throw new ArgumentException("Pickup and delivery QR markers must differ.");
        lock (_gate)
        {
            _store.TaskRoutes[taskId] = new TaskQrSelection
            {
                PickupArUcoId = pickupId,
                DeliveryArUcoId = deliveryId
            };
            SaveLocked();
        }
    }

    // The FE Warehouse Map is the ONLY QR topology. This read-only endpoint
    // presents the same axis-aligned linked graph used by A* routing.
    public object GetDirectedQrGraph()
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        var edges = TraversableQrLinks(map, nodes);
        var qrNodes = nodes.Values.OrderBy(n => n.Id).Select(n => new
        {
            arucoId = n.Id, label = n.Label, row = n.Row, col = n.Col
        }).ToArray();
        var directedEdges = edges.SelectMany(edge => new[]
        {
            new {
                fromArUcoId = edge.From.Id,
                toArUcoId = edge.To.Id,
                travelDirection = DirectionBetween(edge.From, edge.To),
                gridDistance = Math.Abs(edge.From.Row - edge.To.Row) +
                               Math.Abs(edge.From.Col - edge.To.Col)
            },
            new {
                fromArUcoId = edge.To.Id,
                toArUcoId = edge.From.Id,
                travelDirection = DirectionBetween(edge.To, edge.From),
                gridDistance = Math.Abs(edge.From.Row - edge.To.Row) +
                               Math.Abs(edge.From.Col - edge.To.Col)
            }
        }).OrderBy(e => e.fromArUcoId).ThenBy(e => e.toArUcoId).ToArray();
        return new
        {
            mapRevision = map.Revision,
            source = "BACKEND_WAREHOUSE_MAP",
            qrNodes,
            directedEdges,
            ignoredNonQrOrNonAxisLinkCount = map.Links.Count - edges.Count,
            note = "Only explicitly linked, straight QR corridors are navigable. " +
                   "Map editing cannot create a physical road in Webots."
        };
    }

    public string BuildMqttCommand(TaskAssignment assignment)
    {
        long missionId = assignment.Mission?.MissionId
            ?? throw new InvalidOperationException("Assignment has no saved Mission.");
        lock (_gate)
            if (_publishedPayloads.TryGetValue(missionId, out var cached))
                return cached; // MQTT retries must not reset waypoint progress.
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        int pickupId, deliveryId, startId;
        lock (_gate)
        {
            if (!_store.TaskRoutes.TryGetValue(assignment.TaskId, out var choice))
                throw new InvalidOperationException($"Task {assignment.TaskId} has no QR pickup/delivery mapping. Configure it in the task form BEFORE manual assignment.");
            pickupId = choice.PickupArUcoId;
            deliveryId = choice.DeliveryArUcoId;
            if (!_freshRobotPositions.Contains(assignment.Robot.RobotCode) ||
                !_store.RobotPositions.TryGetValue(assignment.Robot.RobotCode, out startId))
                throw new InvalidOperationException($"No fresh Webots READY report for {assignment.Robot.RobotCode}. Start Backend first, then restart the Webots controller and wait for QR READY.");
        }
        if (!nodes.ContainsKey(startId))
            throw new InvalidOperationException($"Robot {assignment.Robot.RobotCode} has unknown start QR {startId}; check the detected camera QR and the saved map.");
        if (!nodes.ContainsKey(pickupId) || !nodes.ContainsKey(deliveryId))
            throw new InvalidOperationException("Task pickup/delivery QR was deleted from the map.");

        var pickupRoute = FindPath(map, nodes, startId, pickupId);
        var deliveryRoute = FindPath(map, nodes, pickupId, deliveryId);
        var fullIds = pickupRoute.Concat(deliveryRoute.Skip(1)).ToList();
        var pickupIndex = pickupRoute.Count - 1;
        var waypoints = fullIds.Select((id, index) => new QrWaypoint
        {
            ArucoId = id,
            Row = nodes[id].Row,
            Col = nodes[id].Col,
            TravelDirection = index == 0 ? null : DirectionBetween(nodes[fullIds[index - 1]], nodes[id]),
            Stage = index == pickupIndex ? "PICKUP" :
                index == fullIds.Count - 1 ? "DELIVERY" :
                index < pickupIndex ? "TO_PICKUP" : "TO_DELIVERY"
        }).ToList();

        var snapshot = new MissionView
        {
            TaskId = assignment.TaskId,
            MissionId = missionId,
            MapRevision = map.Revision,
            RobotCode = assignment.Robot.RobotCode,
            StartArUcoId = startId,
            PickupArUcoId = pickupId,
            DeliveryArUcoId = deliveryId,
            PickupWaypointIndex = pickupIndex,
            Waypoints = waypoints
        };

        var payload = new
        {
            command = "EXECUTE_TASK",
            taskId = assignment.TaskId,
            assignmentId = assignment.AssignmentId,
            missionId,
            pattern = "WAYPOINT_ROUTE",
            mapRevision = map.Revision,
            startArUcoId = startId,
            pickupArUcoId = pickupId,
            deliveryArUcoId = deliveryId,
            metersPerGridCell = _configuration.GetValue("DemoRoute:MetersPerGridCell", 0.35),
            pickupWaypointIndex = pickupIndex,
            routeToPickup = pickupRoute,
            routeToDelivery = deliveryRoute,
            waypoints
        };
        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        lock (_gate)
        {
            if (_publishedPayloads.TryGetValue(missionId, out var cached)) return cached;
            _missions[missionId] = snapshot;
            _publishedPayloads[missionId] = serialized;
        }
        _logger.LogInformation("Demo QR mission {Mission}: start QR{Start}, pickup QR{Pickup}, drop QR{Delivery}, path {Path}",
            missionId, startId, pickupId, deliveryId, string.Join(" -> ", fullIds));
        return serialized;
    }

    public (bool Accepted, string Reason) ReceiveProgress(string robotCode, RouteProgressReport report)
    {
        var map = _navigation.GetMap();
        var qrExists = ReadNodes(map).ContainsKey(report.ArucoId);
        if (!qrExists) return (false, $"QR{report.ArucoId} does not exist on the saved map");
        lock (_gate)
        {
            if (string.Equals(report.Event, "READY", StringComparison.OrdinalIgnoreCase))
            {
                // READY is camera-confirmed by Webots. Use the reported QR for
                // every new mission, even if the previous mission ended at QR7.
                // Fixed-start validation remains opt-in for legacy demo setups.
                if (_configuration.GetValue<bool>("DemoRoute:EnforceFixedStartQr"))
                {
                    var expected = _configuration.GetValue<int>($"DemoRoute:RobotStartQr:{robotCode}", -1);
                    if (expected >= 0 && expected != report.ArucoId)
                        return (false, $"Webots QR{report.ArucoId} disagrees with configured expected start QR{expected}");
                }
                _store.RobotPositions[robotCode] = report.ArucoId;
                _freshRobotPositions.Add(robotCode);
                SaveLocked();
                return (true, "Webots start QR recorded");
            }
            if (!string.Equals(report.Event, "WAYPOINT_REACHED", StringComparison.OrdinalIgnoreCase))
                return (false, "Unsupported QR progress event");
            if (!_missions.TryGetValue(report.MissionId, out var mission) ||
                !string.Equals(mission.RobotCode, robotCode, StringComparison.OrdinalIgnoreCase))
                return (false, "No matching active QR mission");
            if (map.Revision != mission.MapRevision)
                return (false, "Map changed after dispatch. Stop robot and restart with a new task.");
            if (report.WaypointIndex < 0 || report.WaypointIndex >= mission.Waypoints.Count)
                return (false, "Invalid waypoint index");
            if (mission.Waypoints[report.WaypointIndex].ArucoId != report.ArucoId)
                return (false, "QR ID does not match planned waypoint");
            if (report.WaypointIndex > mission.NextWaypointIndex)
                return (false, "Out-of-order waypoint report");
            if (report.WaypointIndex == mission.NextWaypointIndex)
            {
                mission.NextWaypointIndex++;
                mission.LastReachedArUcoId = report.ArucoId;
                mission.PickupReached |= report.WaypointIndex == mission.PickupWaypointIndex;
                mission.DeliveryReached |= report.WaypointIndex == mission.Waypoints.Count - 1;
                _store.RobotPositions[robotCode] = report.ArucoId;
                SaveLocked();
            }
            return (true, mission.DeliveryReached ? "DELIVERY_REACHED" :
                mission.PickupReached ? "ROUTE_PROGRESS_PICKUP_REACHED" : "ROUTE_PROGRESS_OK");
        }
    }

    public object BuildHomeRoute(int start, int home)
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        if (!nodes.ContainsKey(start) || !nodes.ContainsKey(home))
            throw new InvalidOperationException("Unknown home/current QR.");
        var ids = FindPath(map, nodes, start, home);
        return new { startArUcoId = start, homeArUcoId = home, mapRevision = map.Revision,
            waypoints = ids.Select((id, i) => new QrWaypoint { ArucoId = id,
                Row = nodes[id].Row, Col = nodes[id].Col,
                TravelDirection = i == 0 ? null : DirectionBetween(nodes[ids[i - 1]], nodes[id]),
                Stage = "RETURN_HOME" }).ToArray() };
    }

    public bool CanComplete(long? missionId, string robotCode)
    {
        if (!missionId.HasValue) return false;
        lock (_gate) return _missions.TryGetValue(missionId.Value, out var mission) &&
            string.Equals(mission.RobotCode, robotCode, StringComparison.OrdinalIgnoreCase) &&
            mission.PickupReached && mission.DeliveryReached &&
            mission.NextWaypointIndex == mission.Waypoints.Count;
    }

    private static MissionView CloneMission(MissionView x) => new()
    {
        TaskId = x.TaskId, MissionId = x.MissionId, MapRevision = x.MapRevision, RobotCode = x.RobotCode,
        StartArUcoId = x.StartArUcoId, PickupArUcoId = x.PickupArUcoId,
        DeliveryArUcoId = x.DeliveryArUcoId, PickupWaypointIndex = x.PickupWaypointIndex,
        NextWaypointIndex = x.NextWaypointIndex, LastReachedArUcoId = x.LastReachedArUcoId,
        PickupReached = x.PickupReached, DeliveryReached = x.DeliveryReached,
        Waypoints = x.Waypoints.ToArray()
    };

    private static Dictionary<int, Node> ReadNodes(WarehouseMapDocument map)
    {
        var result = new Dictionary<int, Node>();
        for (int r = 0; r < map.Cells.Count; r++)
            for (int c = 0; c < map.Cells[r].Count; c++)
            {
                var cell = map.Cells[r][c];
                if (cell.Type == "qr" && cell.ArucoId.HasValue && !string.IsNullOrWhiteSpace(cell.Label))
                    result.Add(cell.ArucoId.Value, new Node(cell.ArucoId.Value, cell.Label, r, c));
            }
        return result;
    }

    private static bool AxisAligned(Node from, Node to) =>
        (from.Row == to.Row && from.Col != to.Col) ||
        (from.Col == to.Col && from.Row != to.Row);

    private static string DirectionBetween(Node from, Node to)
    {
        if (from.Row == to.Row && from.Col < to.Col) return "E";
        if (from.Row == to.Row && from.Col > to.Col) return "W";
        if (from.Col == to.Col && from.Row < to.Row) return "S";
        if (from.Col == to.Col && from.Row > to.Row) return "N";
        throw new InvalidOperationException($"Map link QR{from.Id} -> QR{to.Id} is not a straight, axis-aligned physical road. Fix the QR grid positions/links in Warehouse Maps.");
    }

    // Shared by the graph API and route planner; there is no robot-side TOPOLOGY.
    private static List<(Node From, Node To)> TraversableQrLinks(
        WarehouseMapDocument map, Dictionary<int, Node> nodes)
    {
        var byLabel = nodes.Values.ToDictionary(n => n.Label, StringComparer.OrdinalIgnoreCase);
        var result = new List<(Node From, Node To)>();
        foreach (var link in map.Links)
        {
            if (link.Count != 2 || !byLabel.TryGetValue(link[0], out var from) ||
                !byLabel.TryGetValue(link[1], out var to)) continue;
            if (!AxisAligned(from, to)) continue;
            result.Add((from, to));
        }
        return result;
    }

    private static List<int> FindPath(WarehouseMapDocument map, Dictionary<int, Node> nodes, int start, int goal)
    {
        if (start == goal) return [start];
        var graph = nodes.Keys.ToDictionary(k => k, _ => new List<int>());
        foreach (var (from, to) in TraversableQrLinks(map, nodes))
        {
            graph[from.Id].Add(to.Id);
            graph[to.Id].Add(from.Id);
        }
        double Dist(int a, int b) => Math.Sqrt(Math.Pow(nodes[a].Row - nodes[b].Row, 2) +
                                               Math.Pow(nodes[a].Col - nodes[b].Col, 2));
        var best = new Dictionary<int, double> { [start] = 0 };
        var previous = new Dictionary<int, int>();
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(start, Dist(start, goal));
        while (queue.TryDequeue(out var at, out var priority))
        {
            if (priority > best[at] + Dist(at, goal) + 1e-9) continue;
            if (at == goal)
            {
                var route = new List<int> { goal };
                while (previous.TryGetValue(route[^1], out var prev)) route.Add(prev);
                route.Reverse();
                return route;
            }
            foreach (var next in graph[at])
            {
                var candidate = best[at] + Dist(at, next);
                if (best.TryGetValue(next, out var old) && candidate >= old - 1e-9) continue;
                best[next] = candidate;
                previous[next] = at;
                queue.Enqueue(next, candidate + Dist(next, goal));
            }
        }
        throw new InvalidOperationException($"No axis-aligned QR path from QR{start} to QR{goal}. Check QR positions and Edit Links in Warehouse Maps, then Save to Backend.");
    }
}
