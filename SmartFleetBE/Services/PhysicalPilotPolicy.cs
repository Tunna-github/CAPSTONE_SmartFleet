namespace SmartFleetBE.Services;

// Explicit allowlist for one supervised, single-link physical pilot mission.
// Start/delivery QR are chosen by the transport task and camera localization;
// not by environment-wide fixed ids. OFF by default; Webots AUTO unchanged.
public static class PhysicalPilotPolicy
{
    public static bool EnabledFor(IConfiguration config, string code) =>
        config.GetValue<bool>("PhysicalPilot:Enabled") &&
        string.Equals(code, config["PhysicalPilot:RobotCode"] ?? "R03",
            StringComparison.OrdinalIgnoreCase);
}
