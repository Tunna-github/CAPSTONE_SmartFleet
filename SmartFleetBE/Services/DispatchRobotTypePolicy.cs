namespace SmartFleetBE.Services;

// Listing, manual confirmation and AUTO dispatch must use the same robot type.
public static class DispatchRobotTypePolicy
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
