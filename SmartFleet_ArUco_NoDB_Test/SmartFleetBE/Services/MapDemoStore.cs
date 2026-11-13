using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartFleetBE.Services;

// TEST-ONLY: Pure in-memory ArUco -> grid configuration.
// ZERO PostgreSQL reads, writes, migrations, or schema changes.
// All marker configurations and last positions disappear on BE restart.
public sealed class MapDemoStore
{
    // Shared by the controller and MQTT worker without touching Program.cs.
    public static MapDemoStore Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<int, DemoMarker> _markers = new();
    private readonly Dictionary<string, DemoScan> _lastScans =
        new(StringComparer.OrdinalIgnoreCase);

    // The existing JetRacer main_code.py and Jupyter demo publish as R01.
    // For a different robot code, change this constant and restart BE.
    public const string TestRobotCode = "R01";
    public const int TestRobotId = 1;
    public const int TestWarehouseId = 1;

    public object ListWarehouses() => new[]
    {
        new
        {
            warehouseId = TestWarehouseId,
            warehouseCode = "DEMO",
            warehouseName = "Map Test (No Database)"
        }
    };

    public object GetSnapshot()
    {
        lock (_gate)
        {
            var markers = _markers.Values
                .OrderBy(m => m.GridRow).ThenBy(m => m.GridColumn)
                .ToArray();

            _lastScans.TryGetValue(TestRobotCode, out var last);
            DemoMarker? mapped = null;
            if (last != null)
                _markers.TryGetValue(last.ArucoId, out mapped);

            var robot = new
            {
                robotId = TestRobotId,
                robotCode = TestRobotCode,
                connectionStatus = last == null ? "OFFLINE" : "ONLINE",
                operationalStatus = "AVAILABLE",
                arucoId = last?.ArucoId,
                cellCode = mapped?.CellCode,
                gridRow = mapped?.GridRow,
                gridColumn = mapped?.GridColumn,
                seenAt = last?.SeenAt
            };

            return new
            {
                warehouseId = TestWarehouseId,
                warehouseCode = "DEMO",
                warehouseName = "Map Test (No Database)",
                markers,
                robots = new[] { robot }
            };
        }
    }

    public bool TryUpsert(
        int arucoId,
        int gridRow,
        int gridColumn,
        out DemoMarker? saved,
        out string? conflict)
    {
        lock (_gate)
        {
            if (_markers.Values.Any(m => m.ArucoId != arucoId &&
                m.GridRow == gridRow && m.GridColumn == gridColumn))
            {
                saved = null;
                conflict = "A different marker is already assigned to this cell.";
                return false;
            }

            saved = new DemoMarker(
                arucoId,
                $"{(char)('A' + gridRow)}{gridColumn}",
                gridRow,
                gridColumn,
                DateTime.UtcNow);
            _markers[arucoId] = saved;
            conflict = null;
            return true;
        }
    }

    public bool Remove(int arucoId)
    {
        lock (_gate)
            return _markers.Remove(arucoId);
    }

    public DemoRobotPosition? ReceiveScan(string robotCode, int arucoId)
    {
        lock (_gate)
        {
            if (!string.Equals(robotCode, TestRobotCode,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            if (!_markers.TryGetValue(arucoId, out var marker))
                return null; // Wait until the admin maps this ID in the web.

            var time = DateTime.UtcNow;
            _lastScans[TestRobotCode] = new DemoScan(arucoId, time);
            return new DemoRobotPosition(
                TestRobotId, TestRobotCode, TestWarehouseId,
                arucoId, marker.CellCode, marker.GridRow, marker.GridColumn, time);
        }
    }

    private sealed record DemoScan(int ArucoId, DateTime SeenAt);
}

public sealed record DemoMarker(
    int ArucoId, string CellCode, int GridRow, int GridColumn, DateTime UpdatedAt);

public sealed record DemoRobotPosition(
    int RobotId, string RobotCode, int WarehouseId,
    int ArucoId, string CellCode, int GridRow, int GridColumn, DateTime SeenAt);
