namespace Forge.Application.Simulation;

/// <summary>
/// Phase-1 visual-story constants for vessels and inbound rail. Tweakable in one place.
/// Capacities are in gel-lot pallets (one cube = one pallet).
/// </summary>
public static class VisualSimulationConstants
{
    /// <summary>Starship cargo capacity in pallets (aligned with a readable train-sized load).</summary>
    public const int StarshipCargoCapacityPallets = 24;

    /// <summary>Inbound train car capacity in pallets (≈ one railcar / container).</summary>
    public const int TrainCarCapacityPallets = 8;

    /// <summary>Simulated time for Approaching → Docked.</summary>
    public static readonly TimeSpan ApproachDuration = TimeSpan.FromMinutes(2);

    /// <summary>Simulated time for Departing → Away after leaving the berth.</summary>
    public static readonly TimeSpan DepartDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Fallback Away duration when ships-per-day is not supplied (legacy short loop).
    /// Preferred path uses ShipsPerDay to space visits across a 24h day.
    /// </summary>
    public static readonly TimeSpan AwayDuration = TimeSpan.FromMinutes(3);

    /// <summary>Minimum dwell while Loading before a partially loaded ship may depart.</summary>
    public static readonly TimeSpan MinLoadingDwell = TimeSpan.FromMinutes(45);

    /// <summary>Minimum cargo before an idle ship may leave (still departs immediately when full).</summary>
    public const int MinDepartLoadedPallets = 3;

    /// <summary>Max pallets loaded onto a ship per tick (keeps the fill visually readable).</summary>
    public const int MaxPalletsLoadedPerTick = 1;

    /// <summary>Inbound train approach duration (sim time) — long enough to see at 60×.</summary>
    public static readonly TimeSpan TrainApproachDuration = TimeSpan.FromMinutes(20);

    /// <summary>Inbound train depart duration (sim time).</summary>
    public static readonly TimeSpan TrainDepartDuration = TimeSpan.FromMinutes(8);

    /// <summary>Keep the consist on the rail this long even after the last pallet is lifted.</summary>
    public static readonly TimeSpan TrainMinUnloadDwell = TimeSpan.FromMinutes(10);

    /// <summary>Max flatbed cars on a single consist.</summary>
    public const int MaxTrainCars = 5;

    /// <summary>Lots (pallets) generated per train visit (before demand weighting).</summary>
    public const int TrainLotsPerVisit = 12;

    /// <summary>Max new PutAway/Pick assignments per tick (keeps breakroom→floor staggered).</summary>
    public const int MaxNewTaskAssignmentsPerTick = 2;

    /// <summary>Fraction of starship visits that arrive with inbound produce (~50%).</summary>
    public const double ShipInboundProduceChance = 0.5;

    /// <summary>Min inbound pallets when a ship brings produce.</summary>
    public const int MinShipInboundPallets = 4;

    /// <summary>Max inbound pallets when a ship brings produce.</summary>
    public const int MaxShipInboundPallets = 12;
}
