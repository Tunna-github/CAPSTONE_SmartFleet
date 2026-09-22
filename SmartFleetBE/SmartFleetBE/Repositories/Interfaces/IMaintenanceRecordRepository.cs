using SmartFleetBE.Model;

namespace SmartFleetBE.Repositories.Interfaces;

public interface IMaintenanceRecordRepository
{
    Task<IReadOnlyCollection<RobotMaintenanceRecord>> GetAllAsync(CancellationToken cancellationToken = default);
}
