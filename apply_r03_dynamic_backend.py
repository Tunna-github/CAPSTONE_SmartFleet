#!/usr/bin/env python3
"""Incremental V1 -> V2 opt-in R03 dynamic adjacent-segment patch.

Run ONLY after apply_r03_mixed_backend.py --apply has succeeded:
    python apply_r03_dynamic_backend.py --check
    python apply_r03_dynamic_backend.py --apply

Retains Webots AUTO/SIMULATION behavior; permits R03 MANUAL tasks for any
camera-confirmed pickup QR directly linked to one non-intersection delivery QR.
No multi-hop route, turn or uncalibrated autonomous movement is enabled.
All matches are checked before any write; originals are backed up.
"""
import argparse
from pathlib import Path
from shutil import copy2

parser = argparse.ArgumentParser()
choice = parser.add_mutually_exclusive_group(required=True)
choice.add_argument('--check', action='store_true')
choice.add_argument('--apply', action='store_true')
args = parser.parse_args()

root = Path.cwd()
route_path = root/'SmartFleetBE/Services/QrDemoRouteService.cs'
policy_path = root/'SmartFleetBE/Services/PhysicalPilotPolicy.cs'
paths = (route_path, policy_path)
for p in paths:
    if not p.is_file():
        raise SystemExit('Missing: {}. Apply and build V1 first, then run this upgrade.'.format(p))
original = {p: p.read_text(encoding='utf-8') for p in paths}
changed = dict(original)

def replace(p, old, new, label):
    matches = changed[p].count(old)
    if matches != 1:
        raise SystemExit('ABORT; no writes: {} expected 1 match, got {} in {}'.format(label, matches, p))
    changed[p] = changed[p].replace(old, new, 1)

old_route = '''                if (PhysicalPilotPolicy.EnabledFor(_configuration, code) &&
                    (start != PhysicalPilotPolicy.StartId(_configuration) ||
                     selection.PickupArUcoId != PhysicalPilotPolicy.StartId(_configuration) ||
                     selection.DeliveryArUcoId != PhysicalPilotPolicy.EndId(_configuration) ||
                     pickupRoute.Count != 1 || pickupRoute[0] != start ||
                     deliveryRoute.Count != 2 || deliveryRoute[0] != start ||
                     deliveryRoute[1] != PhysicalPilotPolicy.EndId(_configuration) ||
                     map.Cells[nodes[start].Row][nodes[start].Col].IsIntersection ||
                     map.Cells[nodes[deliveryRoute[1]].Row][nodes[deliveryRoute[1]].Col].IsIntersection))
                { reason = "R03 physical pilot only supports the direct, non-intersection QR0 -> QR5 lane."; return false; }
'''
new_route = '''                // R03: website chooses station/QR pair; physical robot must be
                // camera-localized AT pickup, with exactly ONE direct corridor.
                // Every other route remains blocked until turning/IMU/obstacle tests.
                if (PhysicalPilotPolicy.EnabledFor(_configuration, code) &&
                    (start != selection.PickupArUcoId ||
                     start == selection.DeliveryArUcoId ||
                     pickupRoute.Count != 1 || pickupRoute[0] != start ||
                     deliveryRoute.Count != 2 || deliveryRoute[0] != start ||
                     deliveryRoute[1] != selection.DeliveryArUcoId ||
                     map.Cells[nodes[start].Row][nodes[start].Col].IsIntersection ||
                     map.Cells[nodes[selection.DeliveryArUcoId].Row][nodes[selection.DeliveryArUcoId].Col].IsIntersection))
                { reason = "R03 physical pilot needs its current pickup QR plus ONE directly linked non-intersection delivery QR."; return false; }
'''
replace(route_path, old_route, new_route, 'remove fixed QR0 -> QR5 route')

old_ready = '''                bool physicalPilotReady = PhysicalPilotPolicy.EnabledFor(_configuration, robotCode) &&
                    report.LocalizationSource == "ARUCO_ONLY" && report.HeadingDeg == null &&
                    report.ReprojectionErrorPx == null && report.RobotStopped &&
                    report.ArucoId == PhysicalPilotPolicy.StartId(_configuration) &&
                    report.StableFrames >= 5 && !cell.IsIntersection;'''
new_ready = '''                bool physicalPilotReady = PhysicalPilotPolicy.EnabledFor(_configuration, robotCode) &&
                    report.LocalizationSource == "ARUCO_ONLY" && report.HeadingDeg == null &&
                    report.ReprojectionErrorPx == null && report.RobotStopped &&
                    report.StableFrames >= 5 && !cell.IsIntersection;'''
replace(route_path, old_ready, new_ready, 'camera READY at any non-intersection QR')

replace(route_path,
        '"Stationary QR0 physical pilot location recorded; heading unknown"',
        '"Stationary physical pilot QR recorded; heading unknown"',
        'localization feedback')

old_policy = '''// Explicit allowlist for the *single* supervised physical QR0->QR5 pilot.
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
new_policy = '''// Explicit allowlist for one supervised, single-link physical pilot mission.
// Start/delivery QR are chosen by the transport task and camera localization;
// not by environment-wide fixed ids. OFF by default; Webots AUTO unchanged.
public static class PhysicalPilotPolicy
{
    public static bool EnabledFor(IConfiguration config, string code) =>
        config.GetValue<bool>("PhysicalPilot:Enabled") &&
        string.Equals(code, config["PhysicalPilot:RobotCode"] ?? "R03",
            StringComparison.OrdinalIgnoreCase);
}
'''
replace(policy_path, old_policy, new_policy, 'remove fixed marker env policy')

print('Checked: 4 exact V1 -> V2 dynamic-pilot anchors matched; no other files modified.')
for p in paths:
    print('  PATCH:',p.relative_to(root))
if args.check:
    print('CHECK ONLY: no changes written. Review the source before --apply.')
else:
    backup = root/'.smartfleet-r03-dynamic-v2-backups'
    for p in paths:
        dest = backup/p.relative_to(root)
        if dest.exists():
            raise SystemExit('Backup already exists; refusing overwrite: '+str(dest))
    for p in paths:
        dest = backup/p.relative_to(root)
        dest.parent.mkdir(parents=True, exist_ok=True)
        copy2(str(p),str(dest))
    for p in paths:
        p.write_text(changed[p], encoding='utf-8')
    print('APPLIED: V2 dynamic direct-link pilot patch; V1 originals backed up in', backup)
    print('NEXT: git diff; dotnet build SmartFleetBE; rebuild Docker backend only.')
