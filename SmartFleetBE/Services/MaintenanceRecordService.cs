using SmartFleetBE.DTOs.Maintenance;
using SmartFleetBE.Repositories.Interfaces;
using SmartFleetBE.Services.Interfaces;

namespace SmartFleetBE.Services;

public sealed class MaintenanceRecordService : IMaintenanceRecordService
{
    private readonly IMaintenanceRecordRepository _maintenanceRecordRepository;

    public MaintenanceRecordService(IMaintenanceRecordRepository maintenanceRecordRepository)
    {
        _maintenanceRecordRepository = maintenanceRecordRepository;
    }

    public async Task<IReadOnlyCollection<MaintenanceRecordResponse>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _maintenanceRecordRepository.GetAllAsync(cancellationToken);

        return records.Select(record => new MaintenanceRecordResponse
        {
            MaintenanceId = record.MaintenanceId,
            RobotId = record.RobotId,
            MaintenanceType = record.MaintenanceType,
            Description = record.Description,
            MaintenanceStatus = record.MaintenanceStatus,
            ScheduledAt = record.ScheduledAt,
            StartedAt = record.StartedAt,
            CompletedAt = record.CompletedAt,
            CreatedAt = record.CreatedAt
        }).ToArray();
    }
}
