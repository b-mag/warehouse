namespace Forge.Application.Simulation;

/// <summary>
/// Phase-1 visual-story constants for vessels and inbound rail. Tweakable in one place.
/// Capacities are in gel-lot pallets (one cube = one pallet).
/// </summary>
public static class VisualSimulationConstants
{
    /// <summary>Starship cargo capacity in pallets (≈ a small outbound freighter load).</summary>
    public const int StarshipCargoCapacityPallets = 100;

    /// <summary>Inbound train car capacity in pallets (≈ one railcar / container).</summary>
    public const int TrainCarCapacityPallets = 40;

    /// <summary>Simulated time for Approaching → Docked.</summary>
    public static readonly TimeSpan ApproachDuration = TimeSpan.FromMinutes(2);

    /// <summary>Simulated time for Departing → Away after leaving the berth.</summary>
    public static readonly TimeSpan DepartDuration = TimeSpan.FromMinutes(2);

    /// <summary>Simulated time spent Away before the next approach attempt.</summary>
    public static readonly TimeSpan AwayDuration = TimeSpan.FromMinutes(3);

    /// <summary>Minimum dwell while Loading before an empty/idle ship may depart.</summary>
    public static readonly TimeSpan MinLoadingDwell = TimeSpan.FromMinutes(1);

    /// <summary>Max pallets loaded onto a ship per tick (keeps the fill visually readable).</summary>
    public const int MaxPalletsLoadedPerTick = 1;
}
