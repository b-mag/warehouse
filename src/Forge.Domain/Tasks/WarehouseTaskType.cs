namespace Forge.Domain.Tasks;

/// <summary>
/// The defined kinds of warehouse work the Engine coordinates (Req 8.1). A
/// <see cref="WarehouseTask"/> is always exactly one of these types.
/// </summary>
public enum WarehouseTaskType
{
    /// <summary>Pick gel lots from storage to fulfill a colony order (FEFO order).</summary>
    Pick = 0,

    /// <summary>Store an inbound gel lot into a compatible temperature zone via slotting.</summary>
    PutAway,

    /// <summary>Load picked gel lots onto a starship within its loading windows.</summary>
    Load,

    /// <summary>Reconcile recorded inventory against physical counts.</summary>
    CycleCount,

    /// <summary>Record a temperature reading for a stored lot / zone.</summary>
    TempCheck,
}
