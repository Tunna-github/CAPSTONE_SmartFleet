namespace SmartFleetBE.Services;

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
