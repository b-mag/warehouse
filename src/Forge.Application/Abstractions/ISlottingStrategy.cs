using Forge.Application.Abstractions.Slotting;
using Forge.Domain.ColdChain;
using Forge.Domain.Gels;

namespace Forge.Application.Abstractions;

/// <summary>
/// A pluggable slotting strategy that selects a compatible zone with capacity for a put-away
/// (design "WMS Core Application abstractions"; Req 16, 20.7). The two Phase-1 strategies —
/// velocity/affinity (default) and naive first-available — implement this (task 18.1); both are
/// deterministic given identical state and inputs (Req 16.5). The active strategy is a live
/// operator parameter (Req 20.7).
/// </summary>
public interface ISlottingStrategy
{
    /// <summary>The strategy's stable key, e.g. <c>"velocity-affinity"</c> or <c>"naive-first-available"</c>.</summary>
    string Key { get; }

    /// <summary>
    /// Select a compatible zone with capacity for <paramref name="lot"/> (of <paramref name="gelType"/>)
    /// among <paramref name="zones"/>, consulting <paramref name="occupancy"/> for live remaining
    /// capacity (Req 16.1, 16.2, 16.3). Returns the chosen zone, or an unslottable result when none
    /// qualify.
    /// </summary>
    SlottingResult SelectZone(
        GelLot lot,
        GelType gelType,
        IReadOnlyCollection<TemperatureZone> zones,
        IZoneOccupancy occupancy);
}
