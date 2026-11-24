namespace SmartFleetBE.Navigation;

public interface INavigationMapStore
{
    int? SelectedWarehouseId => null;
    WarehouseMapDocument LoadOrCreate(WarehouseMapDocument legacyMap);
    void Save(WarehouseMapDocument map, int expectedRevision);
}
