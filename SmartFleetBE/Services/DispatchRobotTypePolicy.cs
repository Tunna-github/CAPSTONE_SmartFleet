namespace SmartFleetBE.Services;

// Listing, manual confirmation and AUTO dispatch must use the same robot type.
public static class DispatchRobotTypePolicy
{
    public static string RequiredType(IConfiguration configuration) =>
        configuration.GetValue<bool>("Simulation:Enabled") ? "SIMULATION" : "PHYSICAL";
}
