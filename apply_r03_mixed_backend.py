#!/usr/bin/env python3
"""Opt-in mixed Webots + R03 straight-lane pilot patch for SmartFleetBE.

Run from the root of your LOCAL CAPSTONE_SmartFleet clone:
    python apply_r03_mixed_backend.py --check
    python apply_r03_mixed_backend.py --apply

The patch deliberately DOES NOT enable PHYSICAL robots for AUTO dispatch.
Changes are checked in memory before ANY file is written; writes are backed up.
Original backend is not supported unless these exact code anchors match.
"""
import argparse
from pathlib import Path
from shutil import copy2

parser = argparse.ArgumentParser()
parser.add_argument('--check', action='store_true')
parser.add_argument('--apply', action='store_true')
args = parser.parse_args()
if args.apply == args.check:
    parser.error('Choose exactly one: --check or --apply')

root = Path.cwd()
p_dispatch = root/'SmartFleetBE/Services/TaskDispatchService.cs'
p_policy = root/'SmartFleetBE/Services/DispatchRobotTypePolicy.cs'
p_route = root/'SmartFleetBE/Services/QrDemoRouteService.cs'
p_pilot = root/'SmartFleetBE/Services/PhysicalPilotPolicy.cs'
paths = [p_dispatch, p_policy, p_route]
for p in paths:
    if not p.is_file():
        raise SystemExit('Run from SmartFleet repo root; missing '+str(p))
if p_pilot.exists():
    raise SystemExit('PhysicalPilotPolicy.cs already exists; review manually, no changes applied')
original = {p:p.read_text(encoding='utf-8') for p in paths}
changed = dict(original)

def replace(path, old, new, desc):
    content = changed[path]
    matches = content.count(old)
    if matches != 1:
        raise RuntimeError('ABORT (no writes): {} has {} anchor matches for {}'.format(path, matches, desc))
    changed[path] = content.replace(old, new, 1)

policy_old='''public static class DispatchRobotTypePolicy
{
    public static string RequiredType(IConfiguration configuration) =>
        configuration.GetValue<bool>("Simulation:Enabled") ? "SIMULATION" : "PHYSICAL";
}
'''
policy_new='''public static class DispatchRobotTypePolicy
{
    public static string RequiredType(IConfiguration configuration) =>
        configuration.GetValue<bool>("Simulation:Enabled") ? "SIMULATION" : "PHYSICAL";

    // Only manual dispatch may use the explicit physical-pilot R03 allowlist.
    // Existing AUTO selection stays SIMULATION in the normal Webots stack.
    public static bool ManualAllows(IConfiguration configuration, string robotType, string robotCode) =>
        string.Equals(robotType, RequiredType(configuration), StringComparison.OrdinalIgnoreCase) ||
        (string.Equals(robotType, "PHYSICAL", StringComparison.OrdinalIgnoreCase) &&
         PhysicalPilotPolicy.EnabledFor(configuration, robotCode));
}
'''
replace(p_policy,policy_old,policy_new,'manual allowlist method')
replace(p_dispatch,
'''            var requiredType = DispatchRobotTypePolicy.RequiredType(_configuration);
            if (robot.RobotType != requiredType) reasons.Add("Robot type must be " + requiredType);''',
'''            if (!DispatchRobotTypePolicy.ManualAllows(_configuration, robot.RobotType, robot.RobotCode))
                reasons.Add("Robot type not allowed for this environment or physical pilot");''','manual candidate filter')
replace(p_dispatch,
'''        var requiredRobotType = DispatchRobotTypePolicy.RequiredType(_configuration);
        if (!string.Equals(
                robot.RobotType,
                requiredRobotType,
                StringComparison.OrdinalIgnoreCase))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict($"Robot must be {requiredRobotType}.");
        }''',
'''        if (!DispatchRobotTypePolicy.ManualAllows(_configuration, robot.RobotType, robot.RobotCode))
        {
            return TransportTaskServiceResult<ManualAssignTaskResponse>
                .Conflict("Robot type is not allowed for this environment or physical pilot.");
        }''','manual assignment revalidation')
replace(p_route,
'''    public sealed record RobotLocalization(int ArucoId, double HeadingDeg, int MapRevision,
        string Source, DateTime ReceivedAtUtc);''',
'''    // Heading is intentionally unknown for the explicitly gated ArUco-only physical pilot.
    public sealed record RobotLocalization(int ArucoId, double? HeadingDeg, int MapRevision,
        string Source, DateTime ReceivedAtUtc);''','nullable physical pilot heading')
replace(p_route,
'''    public void RecordCameraProgress(string code, int arucoId, double? heading, int revision)
    {
        if (heading is not { } yaw || !double.IsFinite(yaw) || yaw is < -180 or > 180) return;
        lock (_gate)
        {
            _store.RobotPositions[code] = arucoId; _freshRobotPositions.Add(code);
            _localizations[code] = new(arucoId, yaw, revision, "ARUCO_IMU", DateTime.UtcNow);
            SaveLocked();
            _navigation.RecordCameraCheckpoint(code, arucoId);
        }
    }''',
'''    public void RecordCameraProgress(string code, int arucoId, double? heading, int revision)
    {
        bool measured = heading is { } yaw && double.IsFinite(yaw) && yaw is >= -180 and <= 180;
        bool pilotConfigured = PhysicalPilotPolicy.EnabledFor(_configuration, code);
        lock (_gate)
        {
            bool pilot = pilotConfigured && _localizations.TryGetValue(code, out var previous) &&
                previous.Source == "ARUCO_ONLY";
            if (!measured && !pilot) return;
            _store.RobotPositions[code] = arucoId; _freshRobotPositions.Add(code);
            _localizations[code] = new(arucoId, measured ? heading : null, revision,
                measured ? "ARUCO_IMU" : "ARUCO_ONLY", DateTime.UtcNow);
            SaveLocked();
            _navigation.RecordCameraCheckpoint(code, arucoId);
        }
    }''','physical pilot progress updates')
replace(p_route,
'''                var deliveryRoute = FindPath(map, nodes, selection.PickupArUcoId, selection.DeliveryArUcoId, robotCode: code);
                if (pickupRoute.Concat(deliveryRoute).Distinct().Any(id =>''',
'''                var deliveryRoute = FindPath(map, nodes, selection.PickupArUcoId, selection.DeliveryArUcoId, robotCode: code);
                if (PhysicalPilotPolicy.EnabledFor(_configuration, code) &&
                    (start != PhysicalPilotPolicy.StartId(_configuration) ||
                     selection.PickupArUcoId != PhysicalPilotPolicy.StartId(_configuration) ||
                     selection.DeliveryArUcoId != PhysicalPilotPolicy.EndId(_configuration) ||
                     pickupRoute.Count != 1 || pickupRoute[0] != start ||
                     deliveryRoute.Count != 2 || deliveryRoute[0] != start ||
                     deliveryRoute[1] != PhysicalPilotPolicy.EndId(_configuration) ||
                     map.Cells[nodes[start].Row][nodes[start].Col].IsIntersection ||
                     map.Cells[nodes[deliveryRoute[1]].Row][nodes[deliveryRoute[1]].Col].IsIntersection))
                { reason = "R03 physical pilot only supports the direct, non-intersection QR0 -> QR5 lane."; return false; }
                if (pickupRoute.Concat(deliveryRoute).Distinct().Any(id =>''','strict QR0 to QR5 route')
replace(p_route,
'''                var cell = map.Cells.SelectMany(row => row).Single(c => c.Type == "qr" && c.ArucoId == report.ArucoId);
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
                return (true, "Webots QR and camera/IMU heading recorded");''',
'''                var cell = map.Cells.SelectMany(row => row).Single(c => c.Type == "qr" && c.ArucoId == report.ArucoId);
                bool cameraImuReady = report.LocalizationSource == "ARUCO_IMU" &&
                    report.HeadingDeg is { } measuredHeading && double.IsFinite(measuredHeading) &&
                    measuredHeading is >= -180 and <= 180 &&
                    report.ReprojectionErrorPx is { } measuredError && double.IsFinite(measuredError) &&
                    measuredError is >= 0 and <= 2 && report.StableFrames >= 5;
                // Deliberately narrow physical pilot: no fabricated heading or fake IMU claim.
                // The operator must align a stationary robot on an isolated STRAIGHT QR0->QR5 lane.
                bool physicalPilotReady = PhysicalPilotPolicy.EnabledFor(_configuration, robotCode) &&
                    report.LocalizationSource == "ARUCO_ONLY" && report.HeadingDeg == null &&
                    report.ReprojectionErrorPx == null && report.RobotStopped &&
                    report.ArucoId == PhysicalPilotPolicy.StartId(_configuration) &&
                    report.StableFrames >= 5 && !cell.IsIntersection;
                if (report.MapRevision != map.Revision || cell.MarkerYawDeg == null ||
                    cell.MarkerSizeM == null || !(cameraImuReady || physicalPilotReady))
                {
                    _freshRobotPositions.Remove(robotCode);
                    _localizations.Remove(robotCode);
                    return (false, "Valid camera/IMU localization, or explicitly configured stationary R03 physical pilot, is required.");
                }
                _store.RobotPositions[robotCode] = report.ArucoId;
                _freshRobotPositions.Add(robotCode);
                _localizations[robotCode] = new(report.ArucoId,
                    cameraImuReady ? report.HeadingDeg : null, map.Revision,
                    cameraImuReady ? "ARUCO_IMU" : "ARUCO_ONLY", DateTime.UtcNow);
                _navigation.RecordCameraCheckpoint(robotCode, report.ArucoId);
                SaveLocked();
                return (true, cameraImuReady ? "Camera/IMU localization recorded" :
                    "Stationary QR0 physical pilot location recorded; heading unknown");''','READY acceptance with limited physical pilot')

policy='''namespace SmartFleetBE.Services;

// Explicit allowlist for the *single* supervised physical QR0->QR5 pilot.
// OFF by default. Does not change Webots, AUTO, or global simulation settings.
public static class PhysicalPilotPolicy
{
    public static bool EnabledFor(IConfiguration config, string code) =>
        config.GetValue<bool>("PhysicalPilot:Enabled") &&
        string.Equals(code, config["PhysicalPilot:RobotCode"] ?? "R03",
            StringComparison.OrdinalIgnoreCase);
    public static int StartId(IConfiguration config) =>
        config.GetValue<int>("PhysicalPilot:StartArUcoId", 0);
    public static int EndId(IConfiguration config) =>
        config.GetValue<int>("PhysicalPilot:DeliveryArUcoId", 5);
}
'''
# Check this exact baseline remains untouched before any write.
for p in paths:
    if changed[p] == original[p]:
        raise SystemExit('No changes to '+str(p))
print('Checked: all 7 exact patch anchors matched current backend files.')
for p in paths:
    print('  PATCH:',p.relative_to(root))
print('  ADD:',p_pilot.relative_to(root))
if args.check:
    print('CHECK ONLY: no file written. Review patch file and make a Git branch before --apply.')
else:
    backup = root/'.smartfleet-r03-backups'
    for p in paths:
        dest = backup/p.relative_to(root)
        dest.parent.mkdir(parents=True, exist_ok=True)
        if dest.exists():
            raise SystemExit('Backup exists, refusing overwrite: '+str(dest))
    for p in paths:
        dest = backup/p.relative_to(root)
        copy2(str(p),str(dest))
    for p in paths:
        p.write_text(changed[p], encoding='utf-8')
    p_pilot.write_text(policy, encoding='utf-8')
    print('APPLIED: source edits saved; originals backed up under .smartfleet-r03-backups')
    print('NEXT: review git diff, run dotnet build, rebuild Docker backend only.')
