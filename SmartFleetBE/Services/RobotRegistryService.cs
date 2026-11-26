using System.Data;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SmartFleetBE.Model;
using SmartFleetBE.Navigation;

namespace SmartFleetBE.Services;

public sealed record RobotRegistryWrite(int WarehouseId, string RobotCode, string RobotType, string Model,
    string ControlSoftware, string? RobotAgentVersion, string? IpAddress, string? MacAddress,
    string MqttClientIdentifier, decimal MaxPayloadWeightKg, bool IsActive, int RegistryRevision);

public sealed class RobotRegistryService(SmartFleetDbContext db, IConfiguration config, NavigationCoordinator navigation)
{
    public async Task<object> Read(CancellationToken ct) => new {
        robots = await db.Robots.AsNoTracking().OrderBy(r => r.RobotCode).Select(r => new {
            r.RobotId, r.WarehouseId, r.RobotCode, r.RobotType, r.Model, r.ControlSoftware, r.RobotAgentVersion,
            r.IpAddress, r.MacAddress, r.MqttClientIdentifier, r.MaxPayloadWeightKg, r.IsActive, r.RegistryRevision,
            r.ConnectionStatus, r.OperationalStatus, r.BatteryPercent, r.LastHeartbeatTime, r.LastTelemetryTime,
            r.CreatedAt }).ToListAsync(ct),
        warehouses = await db.Warehouses.AsNoTracking().OrderBy(w => w.WarehouseCode).Select(w => new {
            w.WarehouseId, code = w.WarehouseCode, name = w.WarehouseName, w.IsActive }).ToListAsync(ct),
        simulationRegistrationAllowed = config.GetValue<bool>("Simulation:Enabled")
    };

    private static string Required(string? text, string name, int max)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > max) throw new ArgumentException($"{name} requires 1..{max} characters.");
        return text;
    }
    public async Task<int> Save(int? id, RobotRegistryWrite request, int userId, CancellationToken ct)
    {
        var code = Required(request.RobotCode, "Robot code", 30).ToUpperInvariant();
        if (!Regex.IsMatch(code, "^[A-Z0-9][A-Z0-9_-]*$")) throw new ArgumentException("Robot code allows letters, digits, underscore and hyphen; no MQTT topic wildcards or slashes.");
        var client = Required(request.MqttClientIdentifier, "MQTT client identifier", 100);
        if (!Regex.IsMatch(client, "^[A-Za-z0-9][A-Za-z0-9._-]*$")) throw new ArgumentException("Invalid MQTT client identifier.");
        var model = Required(request.Model, "Model", 80); var software = Required(request.ControlSoftware, "Control software", 80);
        var agent = request.RobotAgentVersion?.Trim(); var ip = request.IpAddress?.Trim(); var mac = request.MacAddress?.Trim().ToUpperInvariant();
        if (agent?.Length > 40 || !string.IsNullOrEmpty(ip) && (ip.Length > 45 || !IPAddress.TryParse(ip, out _)) ||
            !string.IsNullOrEmpty(mac) && !Regex.IsMatch(mac, "^([0-9A-F]{2}:){5}[0-9A-F]{2}$")) throw new ArgumentException("Invalid IP, MAC address or agent version.");
        if (request.MaxPayloadWeightKg is <= 0 or > 999999.99m || decimal.Round(request.MaxPayloadWeightKg, 2) != request.MaxPayloadWeightKg)
            throw new ArgumentException("Payload capacity must be 0.01..999999.99 kg with at most two decimal places.");
        if (request.RobotType is not ("PHYSICAL" or "SIMULATION")) throw new ArgumentException("Robot type must be PHYSICAL or SIMULATION.");
        if (request.RobotType == "SIMULATION" && !config.GetValue<bool>("Simulation:Enabled")) throw new ArgumentException("Simulation robots can only be registered in the isolated simulation environment.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var robot = id.HasValue ? await db.Robots.SingleOrDefaultAsync(r => r.RobotId == id, ct) ?? throw new KeyNotFoundException() : new Robot();
        if (!await db.Warehouses.AnyAsync(w => w.WarehouseId == request.WarehouseId && w.IsActive, ct)) throw new ArgumentException("Choose an active warehouse.");
        if (await db.Robots.AnyAsync(r => r.RobotId != (id ?? 0) && (r.RobotCode.ToUpper() == code || r.MqttClientIdentifier.ToUpper() == client.ToUpper()), ct))
            throw new InvalidOperationException("Robot code or MQTT client identifier is already registered, including inactive robots.");
        if (id.HasValue) {
            if (request.RegistryRevision != robot.RegistryRevision) throw new InvalidOperationException("Robot configuration changed. Reload before saving.");
            if (code != robot.RobotCode || client != robot.MqttClientIdentifier || request.RobotType != robot.RobotType)
                throw new InvalidOperationException("Robot code, type and MQTT identity are immutable. Register a separate device identity instead.");
            if (await db.TaskAssignments.AnyAsync(a => a.RobotId == id && a.AssignmentStatus == "ACTIVE", ct) ||
                robot.OperationalStatus is "BUSY" or "ASSIGNED" or "EXECUTING" or "CHARGING" ||
                await db.StationBerths.AnyAsync(b => b.RobotId == id, ct) ||
                (!request.IsActive || request.WarehouseId != robot.WarehouseId) && navigation.GetCheckpointOccupants().ContainsKey(robot.RobotCode))
                throw new InvalidOperationException("Resolve assignments, charging, berth reservations and physical camera/corridor occupancy before reconfiguring the robot.");
            if (robot.ConnectionStatus == "ONLINE" || RobotPresencePolicy.IsFresh(robot, DateTime.UtcNow, config))
                throw new InvalidOperationException("Disconnect the robot before changing device configuration.");
            if (request.WarehouseId != robot.WarehouseId && (await db.TaskAssignments.AnyAsync(a => a.RobotId == id, ct) ||
                await db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == id, ct) || await db.RobotIncidents.AnyAsync(i => i.RobotId == id, ct) || robot.LastHeartbeatTime != null))
                throw new InvalidOperationException("A commissioned robot with history cannot move warehouses; preserve its identity and history.");
            if (!request.IsActive && (await db.RobotMaintenanceRecords.AnyAsync(m => m.RobotId == id && (m.MaintenanceStatus == "SCHEDULED" || m.MaintenanceStatus == "IN_PROGRESS"), ct) ||
                await db.RobotIncidents.AnyAsync(i => i.RobotId == id && !i.IsResolved, ct)))
                throw new InvalidOperationException("Resolve maintenance and incidents before deactivating this robot.");
        }
        var before = id.HasValue ? JsonSerializer.Serialize(new { robot.WarehouseId, robot.Model, robot.ControlSoftware, robot.RobotAgentVersion, robot.IpAddress, robot.MacAddress, robot.MaxPayloadWeightKg, robot.IsActive, robot.RegistryRevision }) : null;
        robot.WarehouseId = request.WarehouseId; robot.RobotCode = code; robot.RobotType = request.RobotType;
        robot.Model = model; robot.ControlSoftware = software; robot.RobotAgentVersion = string.IsNullOrEmpty(agent) ? null : agent;
        robot.IpAddress = string.IsNullOrEmpty(ip) ? null : ip; robot.MacAddress = string.IsNullOrEmpty(mac) ? null : mac;
        robot.MqttClientIdentifier = client; robot.MaxPayloadWeightKg = request.MaxPayloadWeightKg; robot.IsActive = request.IsActive;
        robot.UpdatedAt = DateTime.UtcNow;
        if (id.HasValue) robot.RegistryRevision++; else {
            robot.ConnectionStatus = "OFFLINE"; robot.OperationalStatus = "AVAILABLE";
            robot.BatteryPercent = null; robot.CreatedAt = DateTime.UtcNow; db.Robots.Add(robot);
        }
        await db.SaveChangesAsync(ct);
        db.SystemAuditLogs.Add(new() { UserId = userId, ActionCategory = "FLEET", ActionName = id.HasValue ? "UPDATE_ROBOT" : "REGISTER_ROBOT",
            EntityType = "Robot", EntityId = robot.RobotId.ToString(), Description = JsonSerializer.Serialize(new { before, after = request }), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return robot.RobotId;
    }
}
