namespace SmartFleetBE.Constants;

public static class AppRoles
{
    public const string Admin = "Administrator";

    public const string WarehouseOperator = "Warehouse Operator";

    public const string MaintenanceTechnician = "Maintenance Technician";

    public const string AdminOrWarehouseOperator =
        Admin + "," + WarehouseOperator;

    public const string AdminOrMaintenanceTechnician =
        Admin + "," + MaintenanceTechnician;
}