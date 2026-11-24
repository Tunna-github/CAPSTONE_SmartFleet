namespace SmartFleetBE.Navigation;

public interface INavigationMapStore
{
    WarehouseMapDocument LoadOrCreate(WarehouseMapDocument legacyMap);
    void Save(WarehouseMapDocument map, int expectedRevision);
}
