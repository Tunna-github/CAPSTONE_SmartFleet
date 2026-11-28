using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

// Route planning/localization cache for the Development Webots adapter.
// MissionExecutionService persists frozen routes and progress; the saved map supplies topology.
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
        // SMARTFLEET_INTERSECTION_WAYPOINT_V1: configured Backend safety flag.
        public bool IsIntersection { get; init; }
    }

    public sealed class RouteProgressReport
    {
        public string Event { get; set; } = "";
        public string EventId { get; set; } = "";
        public long MissionId { get; set; }
        public int ArucoId { get; set; }
        public int WaypointIndex { get; set; }
        public int? MapRevision { get; set; }
        public double? HeadingDeg { get; set; }
        public string? LocalizationSource { get; set; }
        public double? ReprojectionErrorPx { get; set; }
        public int StableFrames { get; set; }
        public bool RobotStopped { get; set; }
        public Guid? ChargingJobId { get; set; }
        public int Generation { get; set; }
        public string? Reason { get; set; }
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
    private readonly SimulationScene? _scene;
    private readonly ILogger<QrDemoRouteService> _logger;
    private readonly string _statePath;
    private readonly StoreDocument _store;
    private readonly Dictionary<long, MissionView> _missions = [];
    private readonly Dictionary<long, string> _publishedPayloads = [];
    private readonly HashSet<string> _freshRobotPositions = new(StringComparer.OrdinalIgnoreCase);
    public sealed record RobotLocalization(int ArucoId, double? HeadingDeg, int MapRevision,
        string Source, DateTime ReceivedAtUtc);
    private readonly Dictionary<string, RobotLocalization> _localizations = new(StringComparer.OrdinalIgnoreCase);

    public QrDemoRouteService(
        NavigationCoordinator navigation,
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<QrDemoRouteService> logger, SimulationScene? scene = null)
    {
        _navigation = navigation;
        _configuration = configuration;
        _scene = scene;
        _logger = logger;
        _statePath = Path.Combine(environment.ContentRootPath, "App_Data", "qr-demo-state.json");
        _store = File.Exists(_statePath)
            ? JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(_statePath), JsonOptions)
                ?? throw new InvalidDataException("qr-demo-state.json was empty")
            : new StoreDocument();
        _store.TaskRoutes ??= [];
        _store.RobotPositions ??= [];
        foreach (var (code, qr) in _store.RobotPositions) _navigation.RecordCameraCheckpoint(code, qr);
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
        var revision = _navigation.GetMap().Revision;
        lock (_gate) return _store.RobotPositions
            .Where(p => _freshRobotPositions.Contains(p.Key) && _localizations.TryGetValue(p.Key, out var pose) &&
                pose.MapRevision == revision && DateTime.UtcNow - pose.ReceivedAtUtc < TimeSpan.FromSeconds(30))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, RobotLocalization> GetRobotLocalizations()
    {
        var revision = _navigation.GetMap().Revision;
        lock (_gate) return _localizations.Where(p => p.Value.MapRevision == revision &&
            DateTime.UtcNow - p.Value.ReceivedAtUtc < TimeSpan.FromSeconds(30) &&
            (p.Value.Source!=PhysicalLineDemoPolicy.Source || PhysicalLineDemoPolicy.Allows(_configuration,p.Key)))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public bool HasFreshLocalization(string code) => GetRobotLocalizations().ContainsKey(code);

    public void RequireFreshLocalization(string code)
    {
        lock(_gate) { _freshRobotPositions.Remove(code); _localizations.Remove(code); }
    }
    public int CurrentMapRevision => _navigation.GetMap().Revision;

    public void RestoreMission(MissionView snapshot, string payload)
    {
        lock (_gate) { _missions[snapshot.MissionId] = snapshot; _publishedPayloads[snapshot.MissionId] = payload; }
    }
    public void ForgetMission(long missionId)
    {
        lock (_gate) { _missions.Remove(missionId); _publishedPayloads.Remove(missionId); }
    }
    public void RecordCameraProgress(string code, int arucoId, double? heading, int revision)
    {
        lock (_gate)
        {
            if (heading == null && PhysicalLineDemoPolicy.Allows(_configuration,code) &&
                _localizations.TryGetValue(code,out var previous) && previous.Source==PhysicalLineDemoPolicy.Source)
            {
                _store.RobotPositions[code]=arucoId;_freshRobotPositions.Add(code);
                _localizations[code]=new(arucoId,null,revision,PhysicalLineDemoPolicy.Source,DateTime.UtcNow);
                SaveLocked();_navigation.RecordCameraCheckpoint(code,arucoId);return;
            }
            if (heading is not { } yaw || !double.IsFinite(yaw) || yaw is < -180 or > 180) return;
            _store.RobotPositions[code] = arucoId; _freshRobotPositions.Add(code);
            _localizations[code] = new(arucoId, yaw, revision, "ARUCO_IMU", DateTime.UtcNow);
            SaveLocked();
            _navigation.RecordCameraCheckpoint(code, arucoId);
        }
    }

    public object GetLocalizationMap()
    {
        var map = _navigation.GetMap();
        return new { protocolVersion = 1, mapRevision = map.Revision, markerDictionary = map.MarkerDictionary, frame = "MAP_N_W_UP",
            metersPerGridCell = _configuration.GetValue("DemoRoute:MetersPerGridCell", 0.35),
            map.DirectedLinks, map.Links,
            checkpointLinks = TraversableQrLinks(map, ReadNodes(map))
                .Select(edge => new[] { edge.From.Id, edge.To.Id }).ToArray(),
            markers = map.Cells.SelectMany((row, r) => row.Select((cell, c) => new { cell, r, c }))
                .Where(p => p.cell.Type == "qr" && p.cell.ArucoId.HasValue)
                .Select(p => new { arucoId = p.cell.ArucoId, label = p.cell.Label, row = p.r, col = p.c,
                    markerYawDeg = p.cell.MarkerYawDeg, markerSizeM = p.cell.MarkerSizeM, p.cell.IsIntersection }).ToArray() };
    }

    public bool SupportsWarehouse(int warehouseId) => !_navigation.WarehouseId.HasValue || _navigation.WarehouseId == warehouseId;

    public bool CanDispatchTask(long taskId, string code, out string reason, int? warehouseId = null)
    {
        if (warehouseId.HasValue && !SupportsWarehouse(warehouseId.Value))
        { reason = "Task/robot warehouse does not match the configured navigation map warehouse."; return false; }
        reason = "Robot needs a fresh camera/IMU localization on the current map.";
        var map = _navigation.GetMap();
        if (_configuration.GetValue<bool>("Simulation:Enabled") &&
            (_scene == null || !_scene.Matches(map)))
        { reason = "Simulation world and saved QR map do not match. Recommission before dispatch."; return false; }
        if (_configuration.GetValue<bool>("Charging:Enabled") && !GetMapReadiness().ReadyForRouting)
        { reason = "Return-to-charge requires a connected map with valid return lanes. Check Warehouse Maps readiness."; return false; }
        if (!HasFreshLocalization(code)) return false;
        var nodes = ReadNodes(map);
        lock (_gate)
        {
            if (!_store.TaskRoutes.TryGetValue(taskId, out var selection))
            { reason = "Configure this task's pickup and delivery QR before assignment."; return false; }
            try
            {
                var start = _localizations[code].ArucoId;
                var pickupRoute = FindPath(map, nodes, start, selection.PickupArUcoId, robotCode: code);
                var deliveryRoute = FindPath(map, nodes, selection.PickupArUcoId, selection.DeliveryArUcoId, robotCode: code);
                if (_localizations[code].Source!=PhysicalLineDemoPolicy.Source && pickupRoute.Concat(deliveryRoute).Distinct().Any(id =>
                    map.Cells[nodes[id].Row][nodes[id].Col].MarkerYawDeg == null ||
                    map.Cells[nodes[id].Row][nodes[id].Col].MarkerSizeM == null))
                { reason = "Commission every QR marker on this route before assignment."; return false; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            { reason = "No free QR route: another robot occupies a checkpoint/corridor, or the map is disconnected."; return false; }
        }
        reason = "";
        return true;
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

    public void ValidateTaskSelection(int pickupId, int deliveryId)
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        if (!nodes.ContainsKey(pickupId) || !nodes.ContainsKey(deliveryId))
            throw new ArgumentException("Pickup and delivery must be existing QR ArUco IDs in the saved Backend map.");
        if (pickupId == deliveryId)
            throw new ArgumentException("Pickup and delivery QR markers must differ.");
        if (map.Cells[nodes[pickupId].Row][nodes[pickupId].Col].IsIntersection ||
            map.Cells[nodes[deliveryId].Row][nodes[deliveryId].Col].IsIntersection)
            throw new ArgumentException("Pickup/delivery stations must be outside reserved intersections.");
    }

    public void ConfigureTask(long taskId, int pickupId, int deliveryId)
    {
        ValidateTaskSelection(pickupId, deliveryId);
        lock (_gate)
        {
            _store.TaskRoutes.TryGetValue(taskId, out var previous);
            _store.TaskRoutes[taskId] = new TaskQrSelection
            {
                PickupArUcoId = pickupId,
                DeliveryArUcoId = deliveryId
            };
            try { SaveLocked(); }
            catch
            {
                if (previous == null) _store.TaskRoutes.Remove(taskId);
                else _store.TaskRoutes[taskId] = previous;
                throw;
            }
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
        var directedEdges = edges.SelectMany(edge => map.DirectedLinks ? new[] {edge} :
            new[] {edge,(From:edge.To,To:edge.From)}).Select(edge => new {
                fromArUcoId=edge.From.Id,toArUcoId=edge.To.Id,
                travelDirection=DirectionBetween(edge.From,edge.To),
                gridDistance=Math.Abs(edge.From.Row-edge.To.Row)+Math.Abs(edge.From.Col-edge.To.Col)
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

    public sealed record MapReadiness(int MapRevision, int QrNodeCount, int CorridorCount,
        bool ReadyForRouting, IReadOnlyList<string> Issues);

    public MapReadiness GetMapReadiness()
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        var edges = TraversableQrLinks(map, nodes);
        var issues = new List<string>();
        if (nodes.Count < 2)
            issues.Add("Add at least two QR markers with physical ArUco IDs before assigning QR tasks.");
        if (map.Cells.SelectMany(row => row).Any(c => c.Type == "qr" &&
                (c.MarkerYawDeg == null || c.MarkerSizeM == null)))
            issues.Add("Commission each QR marker's rotation and physical size for automatic heading localization.");
        var byLabel = nodes.Values.ToDictionary(n => n.Label, StringComparer.OrdinalIgnoreCase);
        foreach (var link in map.Links)
            if (byLabel.TryGetValue(link[0], out var a) && byLabel.TryGetValue(link[1], out var b) && !AxisAligned(a, b))
                issues.Add($"QR corridor {a.Label} to {b.Label} must be horizontal or vertical.");
        if (nodes.Count > 1)
        {
            var visited = new HashSet<int> { nodes.Keys.First() };
            var queue = new Queue<int>(visited);
            while (queue.TryDequeue(out var id))
                foreach (var (a, b) in edges)
                {
                    var neighbor = a.Id == id ? b.Id : !map.DirectedLinks && b.Id == id ? a.Id : -1;
                    if (neighbor >= 0 && visited.Add(neighbor)) queue.Enqueue(neighbor);
                }
            if (visited.Count != nodes.Count)
                issues.Add("QR markers are disconnected. Link the QR corridors so routes can reach every marker.");
            if(map.DirectedLinks) {
                var reverse=new HashSet<int>{nodes.Keys.First()};var back=new Queue<int>(reverse);
                while(back.TryDequeue(out var id)) foreach(var (a,b) in edges)
                    if(b.Id==id && reverse.Add(a.Id)) back.Enqueue(a.Id);
                if(reverse.Count!=nodes.Count) issues.Add("One-way lanes need a return path from every checkpoint; check exits and charging access.");
            }
        }
        return new(map.Revision, nodes.Count, edges.Count, issues.Count == 0, issues);
    }

    public string BuildMqttCommand(TaskAssignment assignment)
    {
        long missionId = assignment.Mission?.MissionId
            ?? throw new InvalidOperationException("Assignment has no saved Mission.");
        lock (_gate)
            if (_publishedPayloads.TryGetValue(missionId, out var cached))
                return cached; // MQTT retries must not reset waypoint progress.
        if (!CanDispatchTask(assignment.TaskId, assignment.Robot.RobotCode, out var localizationReason, assignment.Robot.WarehouseId))
            throw new InvalidOperationException(localizationReason);
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

        var pickupRoute = FindPath(map, nodes, startId, pickupId, robotCode: assignment.Robot.RobotCode);
        // Preserve arrival direction when optimizing the pickup -> delivery leg.
        var incomingToPickup = pickupRoute.Count > 1
            ? DirectionBetween(nodes[pickupRoute[^2]], nodes[pickupRoute[^1]])
            : null;
        var deliveryRoute = FindPath(map, nodes, pickupId, deliveryId, incomingToPickup, assignment.Robot.RobotCode);
        var fullIds = pickupRoute.Concat(deliveryRoute.Skip(1)).ToList();
        var pickupIndex = pickupRoute.Count - 1;
        var waypoints = fullIds.Select((id, index) => new QrWaypoint
        {
            ArucoId = id,
            Row = nodes[id].Row,
            Col = nodes[id].Col,
                IsIntersection = map.Cells[nodes[id].Row][nodes[id].Col].IsIntersection,
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
            verificationRequired = true,
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
        if (report.Event == "LOCALIZATION_LOST")
        {
            lock (_gate) { _freshRobotPositions.Remove(robotCode); _localizations.Remove(robotCode); }
            return (true, "Localization invalidated; new camera/IMU READY required.");
        }
        var qrExists = ReadNodes(map).ContainsKey(report.ArucoId);
        if (!qrExists)
        {
            if (report.Event == "READY") lock (_gate) { _freshRobotPositions.Remove(robotCode); _localizations.Remove(robotCode); }
            return (false, $"QR{report.ArucoId} does not exist on the saved map");
        }
        lock (_gate)
        {
            if (string.Equals(report.Event, "READY", StringComparison.OrdinalIgnoreCase))
            {
                var cell = map.Cells.SelectMany(row => row).Single(c => c.Type == "qr" && c.ArucoId == report.ArucoId);
                if(report.LocalizationSource==PhysicalLineDemoPolicy.Source)
                {
                    if(!PhysicalLineDemoPolicy.Allows(_configuration,robotCode) || report.MapRevision!=map.Revision ||
                        report.StableFrames<2 || !report.RobotStopped || report.HeadingDeg!=null || report.ReprojectionErrorPx!=null)
                    { _freshRobotPositions.Remove(robotCode);_localizations.Remove(robotCode);return(false,"Invalid or disabled physical line demo checkpoint report."); }
                    _store.RobotPositions[robotCode]=report.ArucoId;_freshRobotPositions.Add(robotCode);
                    _localizations[robotCode]=new(report.ArucoId,null,map.Revision,PhysicalLineDemoPolicy.Source,DateTime.UtcNow);
                    _navigation.RecordCameraCheckpoint(robotCode,report.ArucoId);SaveLocked();
                    return(true,"Camera checkpoint recorded; metric position and heading are unknown.");
                }
                if (report.MapRevision != map.Revision || report.LocalizationSource != "ARUCO_IMU" ||
                    report.HeadingDeg is not { } heading || !double.IsFinite(heading) || heading is < -180 or > 180 ||
                    report.ReprojectionErrorPx is not { } error || !double.IsFinite(error) || error is < 0 or > 2 ||
                    report.StableFrames < 5 || cell.MarkerYawDeg == null || cell.MarkerSizeM == null)
                {
                    _freshRobotPositions.Remove(robotCode);
                    _localizations.Remove(robotCode);
                    return (false, "Valid camera/IMU heading, commissioned marker and current map revision are required.");
                }
                _store.RobotPositions[robotCode] = report.ArucoId;
                _freshRobotPositions.Add(robotCode);
                _localizations[robotCode] = new(report.ArucoId, heading, map.Revision, "ARUCO_IMU", DateTime.UtcNow);
                _navigation.RecordCameraCheckpoint(robotCode, report.ArucoId);
                SaveLocked();
                return (true, "Webots QR and camera/IMU heading recorded");
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
                _navigation.RecordCameraCheckpoint(robotCode, report.ArucoId);
                SaveLocked();
            }
            return (true, mission.DeliveryReached ? "DELIVERY_REACHED" :
                mission.PickupReached ? "ROUTE_PROGRESS_PICKUP_REACHED" : "ROUTE_PROGRESS_OK");
        }
    }

    public object BuildHomeRoute(int start, int home, string? robotCode = null)
    {
        var map = _navigation.GetMap();
        var nodes = ReadNodes(map);
        if (!nodes.ContainsKey(start) || !nodes.ContainsKey(home))
            throw new InvalidOperationException("Unknown home/current QR.");
        var ids = FindPath(map, nodes, start, home, robotCode: robotCode);
        if (ids.Any(id => map.Cells[nodes[id].Row][nodes[id].Col].MarkerYawDeg == null ||
                map.Cells[nodes[id].Row][nodes[id].Col].MarkerSizeM == null) ||
            map.Cells[nodes[start].Row][nodes[start].Col].IsIntersection || map.Cells[nodes[home].Row][nodes[home].Col].IsIntersection)
            throw new InvalidOperationException("Home route requires commissioned markers and start/end outside intersections.");
        if (robotCode != null && _configuration.GetValue<bool>("Navigation:Enabled") &&
            !_navigation.PrepareUnreservedReturnHome(robotCode))
            throw new InvalidOperationException("Active intersection ownership requires physical recovery before returning home.");
        return new { startArUcoId = start, homeArUcoId = home, mapRevision = map.Revision,
            waypoints = ids.Select((id, i) => new QrWaypoint { ArucoId = id,
                Row = nodes[id].Row, Col = nodes[id].Col,
                IsIntersection = map.Cells[nodes[id].Row][nodes[id].Col].IsIntersection,
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

    // SMARTFLEET_WEIGHTED_ASTAR_V1: snapshot-based estimated-time routing.
    // Costs: grid distance proxy + heading changes + current intersection queue.
    // This is NOT collision avoidance, real-time rerouting, or physical ETA.
    // All edge costs are nonnegative, so the Euclidean heuristic remains admissible.
    private List<int> FindPath(
        WarehouseMapDocument map,
        Dictionary<int, Node> nodes,
        int start,
        int goal,
        string? initialHeading = null, string? robotCode = null)
    {
        var blocked = _navigation.GetBlockedQrIds(robotCode).ToHashSet();
        foreach (var n in nodes.Values)
            if (n.Id != start && n.Id != goal && map.Cells[n.Row][n.Col].OperationalRole == "CHARGING")
                blocked.Add(n.Id); // A charging bay is a destination, never a through-road shortcut.
        if (blocked.Contains(start) || blocked.Contains(goal))
            throw new InvalidOperationException("Start or destination QR is occupied by another robot.");
        if (start == goal) return [start];

        var graph = nodes.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var (from, to) in TraversableQrLinks(map, nodes))
        {
            graph[from.Id].Add(to.Id);
            if (!map.DirectedLinks) graph[to.Id].Add(from.Id);
        }

        // The map editor has one source of topology and intersection flags.
        // A snapshot avoids mutable traffic updates halfway through A*.
        var traffic = _navigation.GetIntersections()
            .ToDictionary(x => x.IntersectionArUcoId);

        // Existing active demo missions provide a lightweight demand estimate
        // even before NAV_CONTROL /scan integration is enabled in Webots.
        // Do not treat this estimate as physical occupancy or a safety grant.
        var plannedDemand = new Dictionary<int, int>();
        lock (_gate)
        {
            foreach (var mission in _missions.Values)
            {
                if (mission.NextWaypointIndex >= mission.Waypoints.Count)
                    continue;
                var upcoming = mission.Waypoints
                    .Skip(mission.NextWaypointIndex).Take(4)
                    .Select(w => w.ArucoId).Distinct();
                foreach (var qr in upcoming)
                {
                    if (!traffic.ContainsKey(qr)) continue;
                    plannedDemand.TryGetValue(qr, out var count);
                    plannedDemand[qr] = count + 1;
                }
            }
        }
        double plannedSeconds = Math.Max(0.0, _configuration.GetValue(
            "DemoRoute:Planner:PlannedMissionPenaltySeconds", 2.0));
        double cellSeconds = Math.Max(0.01, _configuration.GetValue(
            "DemoRoute:Planner:SecondsPerGridCell", 1.0));
        double turnSeconds = Math.Max(0.0, _configuration.GetValue(
            "DemoRoute:Planner:TurnPenaltySeconds", 1.5));
        double uTurnSeconds = Math.Max(0.0, _configuration.GetValue(
            "DemoRoute:Planner:UTurnPenaltySeconds", 3.0));
        double queueSeconds = Math.Max(0.0, _configuration.GetValue(
            "DemoRoute:Planner:WaitRobotPenaltySeconds", 4.0));
        double occupiedSeconds = Math.Max(0.0, _configuration.GetValue(
            "DemoRoute:Planner:OccupiedIntersectionPenaltySeconds", 6.0));

        double Euclidean(int a, int b)
        {
            double dr = nodes[a].Row - nodes[b].Row;
            double dc = nodes[a].Col - nodes[b].Col;
            return Math.Sqrt(dr * dr + dc * dc);
        }
        double Heuristic(int id) => Euclidean(id, goal) * cellSeconds;

        double TurnPenalty(string previous, string next)
        {
            if (string.IsNullOrEmpty(previous) || previous == next)
                return 0.0;
            bool opposite = (previous == "N" && next == "S") ||
                            (previous == "S" && next == "N") ||
                            (previous == "E" && next == "W") ||
                            (previous == "W" && next == "E");
            return opposite ? uTurnSeconds : turnSeconds;
        }

        // Incoming heading is part of the search state; simply storing the
        // lowest-cost node would give incorrect routes when turns are costly.
        var origin = (Node: start, Heading: initialHeading ?? "");
        var best = new Dictionary<(int Node, string Heading), double>
        {
            [origin] = 0.0
        };
        var parent = new Dictionary<(int Node, string Heading),
                                    (int Node, string Heading)>();
        var frontier = new PriorityQueue<(int Node, string Heading), double>();
        frontier.Enqueue(origin, Heuristic(start));

        while (frontier.TryDequeue(out var state, out var priority))
        {
            if (!best.TryGetValue(state, out var cost) ||
                priority > cost + Heuristic(state.Node) + 1e-9)
                continue;

            if (state.Node == goal)
            {
                var route = new List<int> { state.Node };
                while (parent.TryGetValue(state, out var previous))
                {
                    state = previous;
                    route.Add(state.Node);
                }
                route.Reverse();
                return route;
            }

            foreach (var next in graph[state.Node])
            {
                if (blocked.Contains(next)) continue;
                // Physical-clearance safety lock: do not plan INTO this QR.
                if (traffic.TryGetValue(next, out var intersection) &&
                    intersection.BlockedPendingPhysicalClearance)
                    continue;

                string direction = DirectionBetween(nodes[state.Node], nodes[next]);
                double delay = 0.0;
                if (intersection != null)
                {
                    delay += intersection.WaitingCount * queueSeconds;
                    delay += intersection.InsideCount * occupiedSeconds;
                    if (plannedDemand.TryGetValue(next, out var count))
                        delay += count * plannedSeconds;
                }
                double candidate = cost +
                    Euclidean(state.Node, next) * cellSeconds +
                    TurnPenalty(state.Heading, direction) + delay;
                var nextState = (Node: next, Heading: direction);
                if (best.TryGetValue(nextState, out var known) &&
                    candidate >= known - 1e-9)
                    continue;
                best[nextState] = candidate;
                parent[nextState] = state;
                frontier.Enqueue(nextState, candidate + Heuristic(next));
            }
        }

        throw new InvalidOperationException(
            $"No usable QR path from QR{start} to QR{goal}. " +
            "Check linked roads and physically blocked intersections in Warehouse Maps.");
    }
}
