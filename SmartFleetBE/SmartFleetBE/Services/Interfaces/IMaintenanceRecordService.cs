using SmartFleetBE.DTOs.Maintenance;

namespace SmartFleetBE.Services.Interfaces;

public interface IMaintenanceRecordService
{
    Task<IReadOnlyCollection<MaintenanceRecordResponse>> GetAllAsync(
        CancellationToken cancellationToken = default);
}
