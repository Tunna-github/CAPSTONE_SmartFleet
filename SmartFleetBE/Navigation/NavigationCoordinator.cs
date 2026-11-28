using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SmartFleetBE.Navigation;

/// <summary>
/// Single-process intersection coordinator for a controlled pilot.
/// Map topology is stored in PostgreSQL; local state retains device config and safety locks.
/// IMPORTANT: run exactly one BE instance. Never treat MQTT alone as a physical e-stop.
/// </summary>
public sealed class NavigationCoordinator
{
    private sealed class RobotState
    {
        public required int RobotId { get; init; }
        public required string RobotCode { get; init; }
        public string LidarStatus { get; set; } = "NO_SCAN_DATA";
        public DateTime LidarAtUtc { get; set; } = DateTime.MinValue;
        public bool SupervisedStraightDemo { get; set; }
        public int? IntersectionId { get; set; }
        public int? CurrentArUcoId { get; set; }
        public int? NextArUcoId { get; set; }
        public int? FollowingArUcoId { get; set; }
        public DateTime LastApproachAtUtc { get; set; } = DateTime.MinValue;
        // Expiring a route report must never turn into an automatic GO.
        public bool RequiresFreshRouteReport { get; set; }
    }

    private sealed class IntersectionState
    {
        public required int ArucoId { get; init; }
        public Dictionary<string, int> Waiting { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime QueueOpenedAtUtc { get; set; } = DateTime.MinValue;
        public string? ActiveRobotCode { get; set; }
        public bool ActiveRobotEntered { get; set; }
        public int LastServedRobotId { get; set; }
        public bool Blocked { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _configPath;
    private readonly ILogger<NavigationCoordinator> _logger;
    private readonly INavigationMapStore? _mapStore;
    private readonly bool _preview;
    // Reports must be refreshed by the robot while approaching (e.g. every 1-2s).
    // A lost robot is removed from the *count*, but granted/entered ownership is
    // never released by TTL: physical clearance or EXIT remains necessary.
    private readonly TimeSpan _approachTtl;
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, RobotState> _robots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IntersectionState> _intersections = [];
    private readonly Dictionary<string, long> _sequence =
        new(StringComparer.OrdinalIgnoreCase);
    private NavigationStoredState _stored;

    public NavigationCoordinator(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        ILogger<NavigationCoordinator> logger,
        INavigationMapStore? mapStore = null)
    {
        _logger = logger;
        _mapStore = mapStore;
        _approachTtl = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue("Navigation:ApproachTtlSeconds", 60), 10, 300));
        _configPath = configuration["Navigation:StateFile"] ??
            Path.Combine(environment.ContentRootPath, "App_Data", "navigation-state.json");

        if (File.Exists(_configPath))
        {
            // Invalid state is a startup error: silently resetting a live lock is unsafe.
            _stored = JsonSerializer.Deserialize<NavigationStoredState>(
                File.ReadAllText(_configPath), JsonOptions)
                ?? throw new InvalidDataException("Empty navigation-state.json");
        }
        else
        {
            _stored = new NavigationStoredState { Map = DefaultMap() };
        }

        _stored.Map ??= DefaultMap();
        NormalizeMap(_stored.Map);
        _stored.RobotConfigs ??= [];
        _stored.IntersectionLocks ??= [];
        _stored.RobotOccupancies = new(_stored.RobotOccupancies ?? [], StringComparer.OrdinalIgnoreCase);
        var validation = ValidateMap(_stored.Map);
        if (validation != null)
            throw new InvalidDataException("Invalid saved navigation map: " + validation);

        if (_mapStore != null)
        {
            _stored.Map = _mapStore.LoadOrCreate(CopyMap(_stored.Map));
            validation = ValidateMap(_stored.Map);
            if (validation != null)
                throw new InvalidDataException("Invalid database navigation map: " + validation);
            NormalizeMap(_stored.Map);
        }

        foreach (var item in _stored.IntersectionLocks)
        {
            var state = GetOrCreateIntersection(item.ArucoId);
            state.LastServedRobotId = item.LastServedRobotId;
            // After restart we cannot prove the last granted robot has exited.
            state.Blocked = item.Blocked || item.GrantedRobotCode != null;
        }

        // Enforce restart interlock in the persisted file before handling messages.
        SaveLocked();
    }

    private NavigationCoordinator(WarehouseMapDocument map, TimeSpan ttl, ILogger<NavigationCoordinator> logger)
    {
        _preview = true;
        _configPath = "";
        _approachTtl = ttl;
        _logger = logger;
        _stored = new NavigationStoredState { Map = CopyMap(map) };
    }

    public (NavigationOperation Result, IntersectionTrafficView? Intersection) PreviewScan(
        int robotId, string code, RobotArUcoScanReport report)
    {
        // Preview owns separate traffic state and never persists or grants a real robot.
        var preview = new NavigationCoordinator(GetMap(), _approachTtl, _logger);
        var result = preview.OnScan(robotId, code, report);
        return (result, report.NextArUcoId.HasValue ? preview.GetIntersection(report.NextArUcoId.Value) : null);
    }

    public int? WarehouseId => _mapStore?.SelectedWarehouseId;

    public void RecordCameraCheckpoint(string robotCode, int arucoId)
    {
        lock (_gate)
        {
            if (FindQrCell(_stored.Map!, arucoId) == null) return;
            // A replay at the departure QR cannot release a corridor already claimed.
            if (_stored.RobotOccupancies.TryGetValue(robotCode, out var old) && old.CurrentArUcoId == arucoId) return;
            _stored.RobotOccupancies[robotCode] = new(arucoId);
            SaveLocked();
        }
    }

    public IReadOnlySet<int> GetBlockedQrIds(string? robotCode)
    {
        lock (_gate) return _stored.RobotOccupancies
            .Where(p => !string.Equals(p.Key, robotCode, StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => p.Value.NextArUcoId is int next
                ? new[] { p.Value.CurrentArUcoId, next } : new[] { p.Value.CurrentArUcoId }).ToHashSet();
    }

    public IReadOnlyDictionary<string, IReadOnlySet<int>> GetCheckpointOccupants()
    {
        lock (_gate) return _stored.RobotOccupancies.ToDictionary(p => p.Key,
            p => (IReadOnlySet<int>)(p.Value.NextArUcoId is int next ? new HashSet<int> {p.Value.CurrentArUcoId, next} : [p.Value.CurrentArUcoId]));
    }

    public bool PrepareUnreservedReturnHome(string robotCode)
    {
        lock (_gate)
        {
            if (_intersections.Values.Any(t => string.Equals(t.ActiveRobotCode, robotCode, StringComparison.OrdinalIgnoreCase)))
                return false; // Granted/entered ownership still requires EXIT or physical recovery.
            foreach (var traffic in _intersections.Values)
            {
                traffic.Waiting.Remove(robotCode);
                if (traffic.Waiting.Count == 0) traffic.QueueOpenedAtUtc = DateTime.MinValue;
            }
            if (_robots.TryGetValue(robotCode, out var robot))
            {
                robot.IntersectionId = null; robot.NextArUcoId = null; robot.RequiresFreshRouteReport = true;
            }
            SaveLocked();
            return true;
        }
    }

    public WarehouseMapDocument GetMap()
    {
        lock (_gate) return CopyMap(_stored.Map!);
    }

    public IReadOnlyDictionary<string, RobotNavigationConfig> GetRobotConfigs()
    {
        lock (_gate)
            return _stored.RobotConfigs.ToDictionary(
                kv => kv.Key, kv => CopyConfig(kv.Value), StringComparer.OrdinalIgnoreCase);
    }

    public RobotNavigationConfig GetRobotConfig(string robotCode)
    {
        lock (_gate) return ConfigFor(robotCode);
    }

    public NavigationOperation SetRobotConfig(string robotCode, RobotNavigationConfig? config)
    {
        if (config == null || config.NormalSpeed is < 0.02m or > 0.50m ||
            config.ApproachSpeed is < 0.01m or > 0.30m ||
            config.ObstacleSpeed is < 0.01m or > 0.20m ||
            config.ApproachSpeed >= config.NormalSpeed ||
            config.ObstacleSpeed > config.ApproachSpeed)
            return NavigationOperation.Fail(
                "Require 0.02<=normal<=0.50, 0.01<=approach<=0.30, " +
                "0.01<=obstacle<=0.20 and obstacle<=approach<normal.");

        lock (_gate)
        {
            _stored.RobotConfigs[robotCode] = CopyConfig(config);
            SaveLocked();
            if (_robots.TryGetValue(robotCode, out var robot))
                return NavigationOperation.Ok([DirectiveFor(robot)]);
            return NavigationOperation.Ok();
        }
    }

    public NavigationOperation ReplaceMap(WarehouseMapDocument? candidate)
    {
        var error = ValidateMap(candidate);
        if (error != null) return NavigationOperation.Fail(error);

        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            if (HasTraffic())
                return NavigationOperation.Fail(
                    "Cannot change the map while intersections have active/waiting robots " +
                    "or a safety lock. Clear the physical area first.", true);
            if (candidate!.Revision != _stored.Map!.Revision)
                return NavigationOperation.Fail(
                    $"Map revision conflict. Current revision: {_stored.Map.Revision}.", true);

            var next = CopyMap(candidate);
            NormalizeMap(next);
            next.Revision++;
            return SaveMapLocked(next);
        }
    }

    public NavigationOperation MarkIntersection(int arucoId, bool enabled)
    {
        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            if (HasTraffic())
                return NavigationOperation.Fail(
                    "Cannot edit intersections while traffic is active or safety-locked.", true);

            var next = CopyMap(_stored.Map!);
            var cell = FindQrCell(next, arucoId);
            if (cell == null)
                return NavigationOperation.Fail(
                    $"ArUco {arucoId} is not a QR cell on this map. Add the QR point first.");

            cell.IsIntersection = enabled;
            next.Revision++;
            return SaveMapLocked(next);
        }
    }

    public IReadOnlyList<IntersectionTrafficView> GetIntersections()
    {
        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            return _stored.Map!.Cells.SelectMany(row => row)
                .Where(cell => cell.Type == "qr" && cell.IsIntersection && cell.ArucoId.HasValue)
                .Select(cell => ToView(GetOrCreateIntersection(cell.ArucoId!.Value)))
                .OrderBy(item => item.IntersectionArUcoId)
                .ToList();
        }
    }

    public IntersectionTrafficView? GetIntersection(int arucoId)
    {
        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            var cell = FindQrCell(_stored.Map!, arucoId);
            return cell is { IsIntersection: true }
                ? ToView(GetOrCreateIntersection(arucoId)) : null;
        }
    }

    public NavigationOperation OnLidar(int robotId, string robotCode, string status, bool supervisedStraightDemo = false)
    {
        if (status is not ("CLEAR" or "DETECTED" or "CRITICAL" or "NO_SCAN_DATA"))
            return NavigationOperation.Fail("Unsupported LiDAR status.");

        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            var robot = GetOrCreateRobot(robotId, robotCode);
            robot.LidarStatus = status;
            robot.LidarAtUtc = DateTime.UtcNow;
            robot.SupervisedStraightDemo = supervisedStraightDemo;

            var outgoing = new Dictionary<string, NavigationDirective>(StringComparer.OrdinalIgnoreCase);
            if (robot.IntersectionId is int id && _intersections.TryGetValue(id, out var traffic))
            {
                var newlyGranted = TryGrant(traffic);
                if (newlyGranted != null) outgoing[newlyGranted.RobotCode] = newlyGranted;
            }
            outgoing[robotCode] = DirectiveFor(robot);
            return NavigationOperation.Ok(outgoing.Values.ToList());
        }
    }

    public bool CanResumeMotion(string robotCode, out string reason)
    {
        lock (_gate) {
            if (!_robots.TryGetValue(robotCode,out var robot) || DateTime.UtcNow-robot.LidarAtUtc>TimeSpan.FromSeconds(2.5) || robot.LidarStatus!="CLEAR") {
                reason="Wait for fresh CLEAR LiDAR before resuming."; return false;
            }
            if (_intersections.Values.Any(i=>i.Blocked && (i.ActiveRobotCode==robotCode || i.Waiting.ContainsKey(robotCode)))) {
                reason="An intersection safety hold requires Maintenance recovery."; return false;
            }
            reason="";return true;
        }
    }

    /// <summary>
    /// Register the *last scanned marker* and the *planned next marker*.
    /// Counting happens only when the next marker is an actual intersection and
    /// the map links the two QR points. Repeated reports are idempotent.
    /// Scans never prove that a granted robot has physically EXITed the crossing.
    /// </summary>
    public NavigationOperation OnScan(int robotId, string robotCode,
        RobotArUcoScanReport? report)
    {
        if (report == null || !report.CurrentArUcoId.HasValue)
            return NavigationOperation.Fail("currentArUcoId is required.");
        int currentId = report.CurrentArUcoId.Value;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            PruneStaleApproachesLocked(now);
            var current = FindQrCell(_stored.Map!, currentId);
            if (current == null)
                return NavigationOperation.Fail(
                    $"Current ArUco {currentId} is not on the configured map.");
            WarehouseMapCell? next = null;
            if (report.NextArUcoId is int nextId)
            {
                next = FindQrCell(_stored.Map!, nextId);
                if (next == null || !AreDirectQrNeighbors(currentId, nextId))
                    return NavigationOperation.Fail(
                        $"Route {currentId} -> {nextId} is not a linked pair of QR markers.");
            }

            var robot = GetOrCreateRobot(robotId, robotCode);
            if (_stored.Map!.DirectedLinks && report.NextArUcoId is int directedNext)
            {
                if (report.FollowingArUcoId is int following && !AreDirectQrNeighbors(directedNext, following))
                    return NavigationOperation.Fail("Invalid ordered lane exit.");
                if (next?.OperationalRole == "NAVIGATION" && report.FollowingArUcoId == null)
                    return NavigationOperation.Fail("Through-lane routes require a following checkpoint; do not stop on a merge.");
            }
            var targetIntersectionId = next is { IsIntersection: true }
                ? report.NextArUcoId : null;
            var outgoing = new Dictionary<string, NavigationDirective>(
                StringComparer.OrdinalIgnoreCase);

            // Route changed before entering. Remove OLD waiting registration.
            // An active/granted robot MUST NOT release a lock via a route scan.
            if (robot.IntersectionId is int oldId && oldId != targetIntersectionId)
            {
                var oldTraffic = GetOrCreateIntersection(oldId);
                if (string.Equals(oldTraffic.ActiveRobotCode, robotCode,
                        StringComparison.OrdinalIgnoreCase))
                    return NavigationOperation.Fail(
                        "Robot has an intersection grant; ENTER then EXIT, or perform physically verified recovery.",
                        true);
                oldTraffic.Waiting.Remove(robotCode);
                if (oldTraffic.Waiting.Count == 0)
                    oldTraffic.QueueOpenedAtUtc = DateTime.MinValue;
                robot.IntersectionId = null;
                if (TryGrant(oldTraffic) is { } oldGrant)
                    outgoing[oldGrant.RobotCode] = oldGrant;
            }

            robot.CurrentArUcoId = currentId;
            robot.NextArUcoId = report.NextArUcoId;
            robot.FollowingArUcoId = report.FollowingArUcoId;
            robot.RequiresFreshRouteReport = false;
            if (targetIntersectionId is int intersectionId)
            {
                var traffic = GetOrCreateIntersection(intersectionId);
                robot.IntersectionId = intersectionId;
                robot.LastApproachAtUtc = now;
                if (!string.Equals(traffic.ActiveRobotCode, robotCode,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (traffic.Waiting.Count == 0)
                        traffic.QueueOpenedAtUtc = now;
                    traffic.Waiting[robotCode] = robotId;
                }
                if (TryGrant(traffic) is { } grant)
                    outgoing[grant.RobotCode] = grant;
            }
            outgoing[robotCode] = DirectiveFor(robot);
            return NavigationOperation.Ok(outgoing.Values.ToList());
        }
    }

    public NavigationOperation OnNavigationEvent(
        int robotId, string robotCode, RobotNavigationEvent? message)
    {
        if (message == null) return NavigationOperation.Fail("Missing navigation event.");
        var name = message.Event.Trim().ToUpperInvariant();
        if (name is not ("APPROACH" or "ENTER" or "EXIT" or "CANCEL"))
            return NavigationOperation.Fail("Event must be APPROACH, ENTER, EXIT or CANCEL.");

        lock (_gate)
        {
            PruneStaleApproachesLocked(DateTime.UtcNow);
            var marker = FindQrCell(_stored.Map!, message.IntersectionArUcoId);
            if (marker is not { IsIntersection: true })
                return NavigationOperation.Fail("Target ArUco is not configured as an intersection.");

            var robot = GetOrCreateRobot(robotId, robotCode);
            var traffic = GetOrCreateIntersection(message.IntersectionArUcoId);
            var outgoing = new Dictionary<string, NavigationDirective>(StringComparer.OrdinalIgnoreCase);

            switch (name)
            {
                case "APPROACH":
                    if (!message.CurrentArUcoId.HasValue ||
                        !AreDirectQrNeighbors(message.CurrentArUcoId.Value, message.IntersectionArUcoId))
                        return NavigationOperation.Fail(
                            "APPROACH must include currentArUcoId of an adjacent QR marker on a map link.");
                    if (robot.IntersectionId.HasValue &&
                        robot.IntersectionId.Value != message.IntersectionArUcoId)
                        return NavigationOperation.Fail("Robot is already committed to another intersection.", true);
                    robot.CurrentArUcoId = message.CurrentArUcoId;
                    robot.NextArUcoId = message.IntersectionArUcoId;
                    robot.LastApproachAtUtc = DateTime.UtcNow;
                    robot.RequiresFreshRouteReport = false;
                    robot.IntersectionId = message.IntersectionArUcoId;
                    if (!string.Equals(traffic.ActiveRobotCode, robotCode, StringComparison.OrdinalIgnoreCase))
                    {
                        if (traffic.Waiting.Count == 0)
                            traffic.QueueOpenedAtUtc = DateTime.UtcNow;
                        traffic.Waiting[robotCode] = robotId;
                    }
                    break;

                case "ENTER":
                    if (!string.Equals(traffic.ActiveRobotCode, robotCode, StringComparison.OrdinalIgnoreCase))
                        return NavigationOperation.Fail("Robot has no grant to enter the intersection.", true);
                    // Once the robot enters, retain the lock even on CRITICAL or disconnect.
                    traffic.ActiveRobotEntered = true;
                    break;

                case "EXIT":
                    if (!string.Equals(traffic.ActiveRobotCode, robotCode, StringComparison.OrdinalIgnoreCase) ||
                        !traffic.ActiveRobotEntered)
                        return NavigationOperation.Fail("Only the entered/granted robot may EXIT.", true);
                    if (!message.CurrentArUcoId.HasValue ||
                        !AreDirectQrNeighbors(message.IntersectionArUcoId, message.CurrentArUcoId.Value))
                        return NavigationOperation.Fail(
                            "EXIT must include currentArUcoId of a QR marker beyond the intersection.");
                    traffic.ActiveRobotCode = null;
                    traffic.ActiveRobotEntered = false;
                    traffic.Waiting.Remove(robotCode);
                    robot.IntersectionId = null;
                    robot.CurrentArUcoId = message.CurrentArUcoId;
                    robot.NextArUcoId = null;
                    robot.RequiresFreshRouteReport = false;
                    break;

                case "CANCEL":
                    if (string.Equals(traffic.ActiveRobotCode, robotCode, StringComparison.OrdinalIgnoreCase))
                        return NavigationOperation.Fail(
                            "A granted robot must EXIT or be physically cleared by an administrator.", true);
                    traffic.Waiting.Remove(robotCode);
                    if (traffic.Waiting.Count == 0)
                        traffic.QueueOpenedAtUtc = DateTime.MinValue;
                    if (robot.IntersectionId == message.IntersectionArUcoId)
                        robot.IntersectionId = null;
                    robot.NextArUcoId = null;
                    robot.RequiresFreshRouteReport = true;
                    break;
            }

            // Persist ownership BEFORE issuing any grant so a restart will fail closed.
            if (name is "ENTER" or "EXIT" or "CANCEL") SaveLocked();
            var grant = TryGrant(traffic);
            if (grant != null) outgoing[grant.RobotCode] = grant;
            outgoing[robotCode] = DirectiveFor(robot);
            return NavigationOperation.Ok(outgoing.Values.ToList());
        }
    }

    public NavigationOperation ManuallyClearIntersection(
        int arucoId, bool confirmedClear, string? reason)
    {
        if (!confirmedClear || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            return NavigationOperation.Fail(
                "PhysicalAreaConfirmedClear=true and an explanation of at least 10 characters are required.");

        lock (_gate)
        {
            if (FindQrCell(_stored.Map!, arucoId) is not { IsIntersection: true })
                return NavigationOperation.Fail("Unknown intersection.");
            var traffic = GetOrCreateIntersection(arucoId);
            var directives = new List<NavigationDirective>();
            var affected = traffic.Waiting.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (traffic.ActiveRobotCode != null) affected.Add(traffic.ActiveRobotCode);
            foreach (var code in affected)
            {
                if (_robots.TryGetValue(code, out var robot))
                {
                    robot.IntersectionId = null;
                    robot.NextArUcoId = null;
                    robot.RequiresFreshRouteReport = true;
                    directives.Add(StopDirective(robot, "MANUAL_INTERSECTION_CLEAR"));
                }
            }
            traffic.ActiveRobotCode = null;
            traffic.ActiveRobotEntered = false;
            traffic.Waiting.Clear();
            traffic.QueueOpenedAtUtc = DateTime.MinValue;
            traffic.Blocked = false;
            SaveLocked();
            _logger.LogWarning(
                "INTERSECTION PHYSICAL CLEAR: ArUco={Id}; reason={Reason}",
                arucoId, reason.Trim());
            return NavigationOperation.Ok(directives);
        }
    }

    private NavigationDirective? TryGrant(IntersectionState traffic)
    {
        if (traffic.Blocked || traffic.ActiveRobotCode != null || traffic.Waiting.Count == 0)
            return null;
        var now = DateTime.UtcNow;
        // Allow simultaneous contenders to report before applying numeric priority.
        // The robot stays at its stop line during this collection window.
        if (now - traffic.QueueOpenedAtUtc < TimeSpan.FromMilliseconds(800))
            return null;
        var eligible = traffic.Waiting
            .Where(pair => _robots.TryGetValue(pair.Key, out var state) &&
                           now - state.LastApproachAtUtc <= _approachTtl &&
                           IsLidarSafe(state, now))
            .Select(pair => (Code: pair.Key, RobotId: pair.Value))
            .OrderBy(item => item.RobotId > traffic.LastServedRobotId ? 0 : 1)
            .ThenBy(item => item.RobotId)
            .ToArray();
        if (eligible.Length == 0) return null;

        var winner = eligible[0];
        traffic.Waiting.Remove(winner.Code);
        if (traffic.Waiting.Count == 0)
            traffic.QueueOpenedAtUtc = DateTime.MinValue;
        traffic.ActiveRobotCode = winner.Code;
        traffic.ActiveRobotEntered = false;
        traffic.LastServedRobotId = winner.RobotId;
        SaveLocked();
        return DirectiveFor(_robots[winner.Code]);
    }

    private NavigationDirective DirectiveFor(RobotState robot)
    {
        var config = ConfigFor(robot.RobotCode);
        var now = DateTime.UtcNow;
        if (robot.RequiresFreshRouteReport)
            return StopDirective(robot, "ROUTE_REPORT_STALE");
        // Fail closed: LiDAR alone must never authorize an unassigned robot to GO.
        if (!robot.CurrentArUcoId.HasValue ||
            (!robot.NextArUcoId.HasValue && !robot.IntersectionId.HasValue))
            return StopDirective(robot, "NO_VALID_ROUTE");
        if (!IsLidarSafe(robot, now))
            return StopDirective(robot, "LIDAR_UNSAFE_OR_STALE");

        if (robot.NextArUcoId is int next && GetBlockedQrIds(robot.RobotCode).Contains(next))
            return StopDirective(robot, "QR_OCCUPIED_BY_OTHER_ROBOT");
        if (_stored.Map!.DirectedLinks && robot.FollowingArUcoId is int exit &&
            FindQrCell(_stored.Map, exit)?.OperationalRole is "PICKUP" or "DELIVERY" or "TRANSFER" or "WAITING" or "CHARGING" &&
            GetBlockedQrIds(robot.RobotCode).Contains(exit))
            return StopDirective(robot, "KEEP_BAY_ENTRY_CLEAR");

        if (robot.IntersectionId is int id)
        {
            var traffic = GetOrCreateIntersection(id);
            if (traffic.Blocked ||
                !string.Equals(traffic.ActiveRobotCode, robot.RobotCode,
                    StringComparison.OrdinalIgnoreCase))
                return StopDirective(robot, "WAIT_FOR_INTERSECTION_GRANT");

            ClaimCorridor(robot);
            return NewDirective(robot, "GO",
                robot.LidarStatus == "DETECTED"
                    ? Math.Min(config.ApproachSpeed, config.ObstacleSpeed)
                    : config.ApproachSpeed,
                true, "INTERSECTION_GRANTED");
        }

        ClaimCorridor(robot);
        return NewDirective(robot, robot.LidarStatus == "DETECTED" ? "SLOW" : "GO",
            robot.LidarStatus == "DETECTED" ? config.ObstacleSpeed : config.NormalSpeed,
            false, robot.LidarStatus == "DETECTED" ? "OBSTACLE_DETECTED" :
                robot.LidarStatus=="NO_SCAN_DATA" ? "SUPERVISED_STRAIGHT_LINE_DEMO_NO_LIDAR" : "CLEAR_ROAD");
    }

    private void ClaimCorridor(RobotState robot)
    {
        if (robot.CurrentArUcoId is not int current || robot.NextArUcoId is not int next) return;
        if (_stored.RobotOccupancies.TryGetValue(robot.RobotCode, out var old))
        {
            // Camera may already have confirmed arrival while an intersection EXIT is pending.
            if (old.CurrentArUcoId != current || old.NextArUcoId == next) return;
        }
        _stored.RobotOccupancies[robot.RobotCode] = new(current, next);
        SaveLocked();
    }

    private NavigationDirective StopDirective(RobotState robot, string reason) =>
        NewDirective(robot, "STOP", 0m, false, reason);

    private NavigationDirective NewDirective(
        RobotState robot, string mode, decimal speed, bool grant, string reason)
    {
        _sequence.TryGetValue(robot.RobotCode, out var n);
        _sequence[robot.RobotCode] = ++n;
        return new NavigationDirective
        {
            RobotCode = robot.RobotCode,
            Mode = mode,
            MaxLinearSpeed = speed,
            IntersectionGranted = grant,
            IntersectionArUcoId = robot.IntersectionId,
            Reason = reason,
            Epoch = _epoch,
            Sequence = n,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(3)
        };
    }

    private bool IsLidarSafe(RobotState robot, DateTime now) =>
        now - robot.LidarAtUtc <= TimeSpan.FromSeconds(2.5) &&
        (robot.LidarStatus is "CLEAR" or "DETECTED" ||
            robot.LidarStatus=="NO_SCAN_DATA" && robot.SupervisedStraightDemo && robot.IntersectionId==null &&
            robot.CurrentArUcoId is int current && robot.NextArUcoId is int next &&
            FindQrCell(_stored.Map!,current) is {IsIntersection:false} && FindQrCell(_stored.Map!,next) is {IsIntersection:false} &&
            AreDirectQrNeighbors(current,next));

    private RobotNavigationConfig ConfigFor(string code) =>
        _stored.RobotConfigs.TryGetValue(code, out var config)
            ? CopyConfig(config) : new RobotNavigationConfig();

    private RobotState GetOrCreateRobot(int robotId, string robotCode)
    {
        if (!_robots.TryGetValue(robotCode, out var robot))
        {
            robot = new RobotState { RobotId = robotId, RobotCode = robotCode };
            _robots[robotCode] = robot;
        }
        return robot;
    }

    private IntersectionState GetOrCreateIntersection(int arucoId)
    {
        if (!_intersections.TryGetValue(arucoId, out var traffic))
        {
            traffic = new IntersectionState { ArucoId = arucoId };
            _intersections[arucoId] = traffic;
        }
        return traffic;
    }

    private IntersectionTrafficView ToView(IntersectionState state)
    {
        var waitingCodes = state.Waiting.OrderBy(pair => pair.Value)
            .Select(pair => pair.Key).ToList();
        var approachingCodes = waitingCodes.ToList();
        if (state.ActiveRobotCode is string active && !state.ActiveRobotEntered)
            approachingCodes.Add(active);
        var approaching = approachingCodes
            .Where(code => _robots.ContainsKey(code))
            .Select(code => _robots[code])
            .Where(robot => robot.CurrentArUcoId.HasValue)
            .OrderBy(robot => robot.RobotId)
            .Select(robot => new ApproachingRobotView
            {
                RobotId = robot.RobotId,
                RobotCode = robot.RobotCode,
                CurrentArUcoId = robot.CurrentArUcoId!.Value,
                NextArUcoId = state.ArucoId,
                Phase = string.Equals(robot.RobotCode, state.ActiveRobotCode,
                    StringComparison.OrdinalIgnoreCase) ? "GRANTED" : "WAITING",
                LastReportedAtUtc = robot.LastApproachAtUtc
            }).ToList();
        var countsBySource = approaching.GroupBy(item => item.CurrentArUcoId)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());
        int inside = state.ActiveRobotEntered && state.ActiveRobotCode != null ? 1 : 0;
        return new IntersectionTrafficView
        {
            IntersectionArUcoId = state.ArucoId,
            ActiveRobotCode = state.ActiveRobotCode,
            ActiveRobotEntered = state.ActiveRobotEntered,
            BlockedPendingPhysicalClearance = state.Blocked,
            LastServedRobotId = state.LastServedRobotId,
            ApproachingCount = approaching.Count,
            WaitingCount = waitingCodes.Count,
            InsideCount = inside,
            TotalRelevantCount = approaching.Count + inside,
            WaitingRobotCodes = waitingCodes,
            ApproachingRobots = approaching,
            ApproachingBySourceArUco = countsBySource
        };
    }

    private void PruneStaleApproachesLocked(DateTime now)
    {
        foreach (var traffic in _intersections.Values)
        {
            // NEVER expire a granted or physically-entered lock via time.
            var stale = traffic.Waiting.Keys.Where(code =>
                !_robots.TryGetValue(code, out var robot) ||
                now - robot.LastApproachAtUtc > _approachTtl).ToArray();
            foreach (var code in stale)
            {
                traffic.Waiting.Remove(code);
                if (_robots.TryGetValue(code, out var robot) &&
                    robot.IntersectionId == traffic.ArucoId)
                {
                    robot.IntersectionId = null;
                    robot.NextArUcoId = null;
                    robot.RequiresFreshRouteReport = true;
                }
            }
            if (traffic.Waiting.Count == 0)
                traffic.QueueOpenedAtUtc = DateTime.MinValue;
        }
    }

    private bool AreDirectQrNeighbors(int first, int second)
    {
        if (first == second) return false;
        var a = FindQrCell(_stored.Map!, first)?.Label;
        var b = FindQrCell(_stored.Map!, second)?.Label;
        return a != null && b != null && _stored.Map!.Links.Any(link =>
            link.Count == 2 &&
            ((string.Equals(link[0], a, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(link[1], b, StringComparison.OrdinalIgnoreCase)) ||
             (!_stored.Map!.DirectedLinks && string.Equals(link[0], b, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(link[1], a, StringComparison.OrdinalIgnoreCase))));
    }

    private bool HasTraffic() => _intersections.Values.Any(state =>
        state.Blocked || state.ActiveRobotCode != null || state.Waiting.Count != 0);

    private NavigationOperation SaveMapLocked(WarehouseMapDocument next)
    {
        try
        {
            // Persist before changing the map used by route planning and MQTT workers.
            _mapStore?.Save(next, _stored.Map!.Revision);
        }
        catch (MapInUseException ex)
        {
            return NavigationOperation.Fail(ex.Message, true);
        }
        catch (StationMapConflictException ex) { return NavigationOperation.Fail(ex.Message, true); }
        catch (DbUpdateConcurrencyException)
        {
            var latest = _mapStore!.LoadOrCreate(CopyMap(_stored.Map!));
            if (ValidateMap(latest) != null)
                throw new InvalidDataException("Database navigation map is invalid.");
            NormalizeMap(latest);
            _stored.Map = latest;
            _intersections.Clear();
            return NavigationOperation.Fail("Database map changed. Reload the map before saving.", true);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException)
        {
            _logger.LogError(ex, "Navigation map database save failed; current map is unchanged.");
            return NavigationOperation.Fail("Map was not saved to the database. Please retry.", unavailable: true);
        }

        _stored.Map = next;
        _intersections.Clear();
        try { SaveLocked(); }
        catch (Exception ex) when (_mapStore != null && ex is IOException or UnauthorizedAccessException)
        {
            // Database commit succeeded. A stale local map mirror must not undo it.
            _logger.LogError(ex, "Map saved to database, but the local navigation mirror could not be updated.");
        }
        return NavigationOperation.Ok();
    }

    private void SaveLocked()
    {
        if (_preview) return;
        _stored.IntersectionLocks = _intersections.Values
            .Where(item => item.Blocked || item.ActiveRobotCode != null || item.LastServedRobotId != 0)
            .Select(item => new StoredIntersectionLock
            {
                ArucoId = item.ArucoId,
                GrantedRobotCode = item.ActiveRobotCode,
                LastServedRobotId = item.LastServedRobotId,
                Blocked = item.Blocked
            }).ToList();
        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("Invalid navigation state path.");
        Directory.CreateDirectory(directory);
        var temp = _configPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_stored, JsonOptions));
        File.Move(temp, _configPath, true);
    }

    private static WarehouseMapDocument CopyMap(WarehouseMapDocument map) =>
        JsonSerializer.Deserialize<WarehouseMapDocument>(JsonSerializer.Serialize(map, JsonOptions), JsonOptions)!;

    private static RobotNavigationConfig CopyConfig(RobotNavigationConfig config) => new()
    {
        NormalSpeed = config.NormalSpeed,
        ApproachSpeed = config.ApproachSpeed,
        ObstacleSpeed = config.ObstacleSpeed
    };

    private static WarehouseMapCell? FindQrCell(WarehouseMapDocument map, int arucoId) =>
        map.Cells.SelectMany(row => row).FirstOrDefault(cell =>
            string.Equals(cell.Type, "qr", StringComparison.OrdinalIgnoreCase) &&
            cell.ArucoId == arucoId);

    private static int? IdFromLabel(string? label)
    {
        var match = Regex.Match(label ?? "", @"^QR0*([0-9]+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    private static void NormalizeMap(WarehouseMapDocument map)
    {
        foreach (var cell in map.Cells.SelectMany(row => row))
        {
            cell.Type = cell.Type.Trim().ToLowerInvariant();
            cell.Label = cell.Label?.Trim();
            cell.DisplayName = string.IsNullOrWhiteSpace(cell.DisplayName) ? null : cell.DisplayName.Trim();
            if (cell.Type == "qr") cell.ArucoId ??= IdFromLabel(cell.Label);
            if (map.DirectedLinks && cell.Type == "qr") cell.OperationalRole ??= "NAVIGATION";
        }
        map.Links = map.Links.Select(link => link.Select(value => value.Trim()).ToList()).ToList();
    }

    private static string? ValidateMap(WarehouseMapDocument? candidate)
    {
        if (candidate == null) return "Missing map.";
        if (candidate.Rows != 18 || candidate.Columns != 28 || candidate.Revision < 1)
            return "The current frontend requires an 18x28 map with revision >= 1.";
        if (candidate.Cells == null || candidate.Cells.Count != candidate.Rows ||
            candidate.Cells.Any(row => row == null || row.Count != candidate.Columns))
            return "Expected exactly 18 rows of 28 cells.";
        if (candidate.Links == null || candidate.Links.Count > 1500)
            return "Invalid link array.";
        if (candidate.MarkerDictionary is not ("DICT_4X4_50" or "DICT_4X4_250"))
            return "Unsupported ArUco dictionary.";
        var maximumMarkerId = candidate.MarkerDictionary == "DICT_4X4_250" ? 249 : 49;

        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<int>();
        foreach (var cell in candidate.Cells.SelectMany(row => row))
        {
            if (cell == null || cell.Type == null) return "Invalid map cell.";
            var type = cell.Type.Trim().ToLowerInvariant();
            if (type is not ("wall" or "floor" or "station" or "qr" or "charge"))
                return "Unknown map cell type.";
            if (type != "qr" && (cell.DisplayName != null || cell.OperationalRole != null))
                return "Only QR checkpoints can have a display name or role.";
            if (type is "wall" or "floor")
            {
                if (cell.ArucoId != null || cell.IsIntersection || !string.IsNullOrEmpty(cell.Label) ||
                    cell.MarkerYawDeg != null || cell.MarkerSizeM != null)
                    return "Empty floor/wall cells cannot have labels or ArUco metadata.";
                continue;
            }
            if (string.IsNullOrWhiteSpace(cell.Label) || cell.Label.Length > 24 ||
                !Regex.IsMatch(cell.Label, @"^[A-Za-z0-9_-]+$") ||
                !labels.Add(cell.Label.Trim()))
                return "Station/QR/charge labels must be unique, nonempty and alphanumeric.";
            if (type == "qr")
            {
                if (cell.DisplayName is { Length: > 100 } || cell.DisplayName?.Any(char.IsControl) == true)
                    return "Checkpoint display names must be at most 100 characters on one line.";
                if (cell.OperationalRole is not (null or "NAVIGATION" or "PICKUP" or "DELIVERY" or "TRANSFER" or "WAITING" or "CHARGING"))
                    return "Invalid checkpoint role. Configure intersections using the intersection flag.";
                if (cell.IsIntersection && cell.OperationalRole is not (null or "NAVIGATION"))
                    return "Intersections cannot have an operational station role.";
                var id = cell.ArucoId ?? IdFromLabel(cell.Label);
                if (!id.HasValue || id.Value < 0 || id.Value > maximumMarkerId || !ids.Add(id.Value))
                    return $"Each QR requires a unique ArUco ID 0..{maximumMarkerId} ({candidate.MarkerDictionary}).";
                if (cell.MarkerYawDeg is { } yaw && (!double.IsFinite(yaw) || yaw is < -180 or > 180))
                    return "Marker yaw must be between -180 and 180 degrees.";
                if (cell.MarkerSizeM is { } size && (!double.IsFinite(size) || size is < 0.005 or > 2))
                    return "Marker size must be between 0.005 and 2 meters.";
            }
            else if (cell.ArucoId.HasValue || cell.IsIntersection || cell.MarkerYawDeg != null || cell.MarkerSizeM != null)
                return "Only QR cells can have ArUco IDs or be intersections.";
        }

        var uniqueLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in candidate.Links)
        {
            if (link == null || link.Count != 2 ||
                string.IsNullOrWhiteSpace(link[0]) || string.IsNullOrWhiteSpace(link[1]))
                return "Every link must be [fromLabel, toLabel].";
            var a = link[0].Trim();
            var b = link[1].Trim();
            if (!labels.Contains(a) || !labels.Contains(b) ||
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return "Each link must connect two distinct existing map points.";
            var key = candidate.DirectedLinks || string.Compare(a, b, StringComparison.OrdinalIgnoreCase) < 0
                ? a + "::" + b : b + "::" + a;
            if (!uniqueLinks.Add(key)) return "Duplicate map link.";
        }
        if (candidate.DirectedLinks)
        {
            var points = candidate.Cells.SelectMany((row,r) => row.Select((cell,c) => new {cell,r,c}))
                .Where(p => p.cell.Type == "qr").ToDictionary(p => p.cell.Label!, StringComparer.OrdinalIgnoreCase);
            if(candidate.Links.Any(e=>!points.ContainsKey(e[0]) || !points.ContainsKey(e[1])))
                return "Directed lanes must connect commissioned QR checkpoints.";
            foreach (var edge in candidate.Links)
            {
                if (!points.TryGetValue(edge[0],out var a) || !points.TryGetValue(edge[1],out var b))
                    return "Directed lanes must connect commissioned QR checkpoints.";
                if (a.r != b.r && a.c != b.c) return "A lane must be straight and axis aligned. Add a turning checkpoint.";
                if (points.Values.Any(p => p != a && p != b && ((a.r == b.r && p.r == a.r && p.c > Math.Min(a.c,b.c) && p.c < Math.Max(a.c,b.c)) ||
                    (a.c == b.c && p.c == a.c && p.r > Math.Min(a.r,b.r) && p.r < Math.Max(a.r,b.r)))))
                    return "A lane cannot skip an intermediate checkpoint. Split the ordered link.";
                if (!a.cell.IsIntersection && !b.cell.IsIntersection && (a.cell.OperationalRole is null or "NAVIGATION") && (b.cell.OperationalRole is null or "NAVIGATION") &&
                    candidate.Links.Any(e => e[0].Equals(edge[1],StringComparison.OrdinalIgnoreCase) && e[1].Equals(edge[0],StringComparison.OrdinalIgnoreCase)))
                    return "Opposing through lanes require physically separate checkpoints. Shared spurs are only allowed at operational bays.";
                foreach(var other in candidate.Links) {
                    var c=points[other[0]];var d=points[other[1]];
                    if(a.r==b.r && c.c==d.c && a.r>Math.Min(c.r,d.r) && a.r<Math.Max(c.r,d.r) && c.c>Math.Min(a.c,b.c) && c.c<Math.Max(a.c,b.c))
                        return "Crossing lanes require a shared checkpoint and explicit traffic coordination.";
                }
            }
        }
        return null;
    }

    private static WarehouseMapDocument DefaultMap()
    {
        var cells = Enumerable.Range(0, 18).Select(_ =>
            Enumerable.Range(0, 28).Select(_ => new WarehouseMapCell()).ToList()).ToList();
        (int R, int C, string Label)[] points =
        [
            (6, 9, "S1"), (6, 13, "S2"), (6, 17, "S3"),
            (9, 9, "S4"), (9, 13, "S5"), (9, 17, "S6"),
            (12, 9, "S7"), (12, 13, "S8"), (12, 17, "S9")
        ];
        foreach (var p in points)
            cells[p.R][p.C] = new WarehouseMapCell { Type = "station", Label = p.Label };
        cells[15][13] = new WarehouseMapCell { Type = "charge", Label = "CHG1" };
        var edges = new[]
        {
            ("S1", "S2"), ("S2", "S3"), ("S4", "S5"), ("S5", "S6"),
            ("S7", "S8"), ("S8", "S9"), ("S1", "S4"), ("S4", "S7"),
            ("S2", "S5"), ("S5", "S8"), ("S3", "S6"), ("S6", "S9"),
            ("S8", "CHG1")
        };
        return new WarehouseMapDocument
        {
            Cells = cells,
            Links = edges.Select(e => new List<string> { e.Item1, e.Item2 }).ToList()
        };
    }
}
