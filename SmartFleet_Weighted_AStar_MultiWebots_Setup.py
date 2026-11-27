#!/usr/bin/env python3
"""SmartFleet CAPSTONE: weighted A* planner + per-Webots-robot MQTT identity.

Local, guarded installer for a checked-out SmartFleet repository and an optional
actual Webots world controller. Does not access the network, edit PostgreSQL,
change FE, commit or push. Backs up every file to system temporary storage.

Windows (from CAPSTONE_SmartFleet root):
  py -3 SmartFleet_Weighted_AStar_MultiWebots_Setup.py --repo . --webots "C:\\...\\controllers\\my_controller_testing_fleet\\my_controller_testing_fleet.py"

Webots robot controllerArgs:
  R01: --robot-code=R01 --start-qr=0 --start-heading=S
  R02: --robot-code=R02 --start-qr=5 --start-heading=S

IMPORTANT: This enables independent simulated robots and cost-aware route planning.
It does NOT enforce collision avoidance/intersection permissions in Webots.
Do not route two robots concurrently through shared intersections until the
NAV_CONTROL/STOP/GO/ENTER/EXIT control protocol is integrated and tested.
"""

from __future__ import annotations

import argparse
import datetime as dt
import re
import shutil
import sys
import tempfile
from pathlib import Path

MARKER_BE = "SMARTFLEET_WEIGHTED_ASTAR_V1"
MARKER_WEBOTS = "SMARTFLEET_MULTI_ROBOT_ARGS_V1"
ROUTE_PATH = Path("SmartFleetBE/Services/QrDemoRouteService.cs")
WEBOTS_REPO_PATH = Path("Webots_Full_Mission_Controller/my_controller_testing_fleet.py")

WEIGHTED_ASTAR = r'''    // SMARTFLEET_WEIGHTED_ASTAR_V1: snapshot-based estimated-time routing.
    // Costs: grid distance proxy + heading changes + current intersection queue.
    // This is NOT collision avoidance, real-time rerouting, or physical ETA.
    // All edge costs are nonnegative, so the Euclidean heuristic remains admissible.
    private List<int> FindPath(
        WarehouseMapDocument map,
        Dictionary<int, Node> nodes,
        int start,
        int goal,
        string? initialHeading = null)
    {
        if (start == goal) return [start];

        var graph = nodes.Keys.ToDictionary(id => id, _ => new List<int>());
        foreach (var (from, to) in TraversableQrLinks(map, nodes))
        {
            graph[from.Id].Add(to.Id);
            graph[to.Id].Add(from.Id);
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
    }'''

WEBOTS_ARGS = '''# SMARTFLEET_MULTI_ROBOT_ARGS_V1
# Each simulated robot is a separate process but shares this source file.
# Configure Robot.controllerArgs per robot in Webots, for example:
#   --robot-code=R01 --start-qr=0 --start-heading=S
#   --robot-code=R02 --start-qr=5 --start-heading=S
# The fallback below preserves the original one-robot R01 demo; it must NEVER
# be relied upon when multiple robots are present.
def _sf_controller_arg(key, default):
    prefix = "--{}=".format(key)
    matches = [value[len(prefix):] for value in sys.argv[1:]
               if value.startswith(prefix)]
    if len(matches) > 1:
        raise ValueError("Duplicated Webots controller argument " + prefix)
    return matches[0] if matches else default


ROBOT_CODE = _sf_controller_arg("robot-code", "R01").upper()
if not ROBOT_CODE.startswith("R") or not ROBOT_CODE[1:].isdigit():
    raise ValueError("Invalid Webots --robot-code: " + repr(ROBOT_CODE))
if not any(value.startswith("--robot-code=") for value in sys.argv[1:]):
    print("[CONFIG WARNING] controllerArgs has no --robot-code; using R01! "
          "Set a unique code on EVERY Webots Robot before adding more.")'''


def load_source(path: Path) -> tuple[str, bytes, str]:
    b = path.read_bytes()
    bom = b'\xef\xbb\xbf' if b.startswith(b'\xef\xbb\xbf') else b''
    t = b[len(bom):].decode('utf-8')
    nl = '\r\n' if b'\r\n' in b else '\n'
    return t.replace('\r\n', '\n'), bom, nl


def save_source(path: Path, code: str, bom: bytes, newline: str) -> None:
    if newline == '\r\n':
        code = code.replace('\n', '\r\n')
    tmp = path.with_name(path.name + '.smartfleet_tmp')
    tmp.write_bytes(bom + code.encode('utf-8'))
    tmp.replace(path)


def substitute_once(text: str, old: str, new: str, what: str) -> str:
    if text.count(old) != 1:
        raise ValueError(f'{what}: expected 1 exact anchor, found {text.count(old)}. No files changed.')
    return text.replace(old, new, 1)


def replace_method(s: str) -> str:
    if MARKER_BE in s:
        return s
    m = re.search(r'\bprivate\s+(?:static\s+)?List<int>\s+FindPath\s*\(', s)
    if not m:
        raise ValueError('Backend FindPath signature is different; refusing to overwrite it.')
    begin = s.find('{', m.end())
    if begin < 0:
        raise ValueError('FindPath body missing.')
    depth = 0
    end = None
    for i in range(begin, len(s)):
        if s[i] == '{':
            depth += 1
        elif s[i] == '}':
            depth -= 1
            if depth == 0:
                end = i + 1
                break
    if end is None:
        raise ValueError('FindPath body is unbalanced.')
    # Replace indentation at the start too, not just signature.
    line_start = s.rfind('\n', 0, m.start()) + 1
    if s[line_start:m.start()].strip():
        line_start = m.start()
    return s[:line_start] + WEIGHTED_ASTAR + s[end:]


def patch_backend(s: str) -> str:
    if MARKER_BE in s:
        return s
    s = substitute_once(s,
        '''        var pickupRoute = FindPath(map, nodes, startId, pickupId);
        var deliveryRoute = FindPath(map, nodes, pickupId, deliveryId);''',
        '''        var pickupRoute = FindPath(map, nodes, startId, pickupId);
        // Preserve arrival direction when optimizing the pickup -> delivery leg.
        var incomingToPickup = pickupRoute.Count > 1
            ? DirectionBetween(nodes[pickupRoute[^2]], nodes[pickupRoute[^1]])
            : null;
        var deliveryRoute = FindPath(map, nodes, pickupId, deliveryId, incomingToPickup);''',
        'two-leg routing')
    return replace_method(s)


def patch_webots(s: str) -> str:
    if MARKER_WEBOTS in s:
        return s
    if 'import sys\n' not in s:
        s = substitute_once(s, 'import os\n', 'import os\nimport sys\n', 'Webots import')
    m = re.search(r'(?m)^ROBOT_CODE = [^\n]+$', s)
    if not m:
        raise ValueError('Webots ROBOT_CODE = assignment not found; refusing edit.')
    s = s[:m.start()] + WEBOTS_ARGS + s[m.end():]
    s = re.sub(r'(?m)^FIRST_RUN_CALIBRATION_QR\s*=\s*0(?:\s*#.*)?$',
               'FIRST_RUN_CALIBRATION_QR = int(_sf_controller_arg("start-qr", "0"))', s, count=1)
    s = re.sub(r'(?m)^FIRST_RUN_HEADING\s*=\s*["\']S["\'](?:\s*#.*)?$',
               'FIRST_RUN_HEADING = _sf_controller_arg("start-heading", "S").upper()', s, count=1)
    if 'FIRST_RUN_HEADING = _sf_controller_arg' not in s or \
       'FIRST_RUN_CALIBRATION_QR = int(_sf_controller_arg' not in s:
        raise ValueError('Webots first-run orientation configuration differs; refusing edit.')
    s = substitute_once(s, 'FIRST_RUN_HEADING = _sf_controller_arg("start-heading", "S").upper()',
        'FIRST_RUN_HEADING = _sf_controller_arg("start-heading", "S").upper()\n'
        'if FIRST_RUN_HEADING not in ("N", "S", "E", "W") or FIRST_RUN_CALIBRATION_QR < 0:\n'
        '    raise ValueError("Invalid Webots --start-heading or --start-qr")',
        'Webots calibration validation')
    return s


def build_plan(repo: Path, webots: Path | None) -> list[tuple[Path, str, bytes, str, bytes]]:
    targets = [(repo / ROUTE_PATH, patch_backend),
               (repo / WEBOTS_REPO_PATH, patch_webots)]
    if webots is not None and webots.resolve() != (repo / WEBOTS_REPO_PATH).resolve():
        targets.append((webots, patch_webots))
    plan = []
    for path, patch in targets:
        if not path.is_file():
            raise FileNotFoundError(f'Missing {path}')
        src, bom, nl = load_source(path)
        dest = patch(src)
        plan.append((path, dest, bom, nl, path.read_bytes()))
    return plan


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--repo', required=True, type=Path, help='SmartFleet repository root')
    ap.add_argument('--webots', type=Path, help='Actual Webots my_controller_testing_fleet.py, optionally')
    ap.add_argument('--dry-run', action='store_true', help='Check all source anchors without writing')
    args = ap.parse_args()
    repo = args.repo.resolve()
    try:
        plan = build_plan(repo, args.webots)
        changes = [p for p in plan if p[4] != (p[2] + p[1].replace('\n', p[3]).encode('utf-8'))]
        if not changes:
            print('Already installed; no changes needed.')
            return 0
        print('Planned file updates:')
        for path, _, _, _, _ in changes:
            print('  ' + str(path))
        if args.dry_run:
            print('DRY RUN: no changes written.')
            return 0
        stamp = dt.datetime.now().strftime('%Y%m%d_%H%M%S')
        backup = Path(tempfile.gettempdir()) / 'SmartFleet_Weighted_AStar_MultiWebots_backups' / stamp
        backup.mkdir(parents=True, exist_ok=False)
        # All patches/anchors verified BEFORE the first write.
        for i, (path, code, bom, nl, original) in enumerate(changes):
            target = backup / f'{i+1}_{path.name}.bak'
            target.write_bytes(original)
            save_source(path, code, bom, nl)
        print('APPLIED. Backup folder: ' + str(backup))
        print('NO database changes. NO Git commits/push. NO world placement edits.')
        print('Build Backend; configure each Robot.controllerArgs in Webots.')
        return 0
    except (OSError, UnicodeError, ValueError) as exc:
        print('REFUSED: ' + str(exc), file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
