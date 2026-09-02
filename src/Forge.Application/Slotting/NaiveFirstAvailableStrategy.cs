using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Slotting;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Slotting;

namespace Forge.Application.Slotting;

/// <summary>
/// The Phase-1 "naive first-available" slotting strategy (Req 16, 20.7): among the zones whose
/// allowable range contains the gel type's storage range and that have available capacity, it
/// selects the compatible zone with the smallest <see cref="ZoneId"/> and applies no velocity /
/// travel-time preference (that is the job of <see cref="VelocityAffinitySlottingStrategy"/>).
/// <para>
/// It builds directly on the strategy-agnostic domain slotting primitives
/// (<see cref="SlottingCandidates.CompatibleZones(TemperatureRange, System.Collections.Generic.IEnumerable{TemperatureZone})"/>
/// + <see cref="ZoneIdComparer"/>): compatibility (zone allowable range contains gel storage range)
/// and available capacity are decided in the Domain, keeping slotting logic in the Engine (Req 16.4).
/// </para>
/// <para>
/// <b>Live occupancy.</b> A zone's own <see cref="TemperatureZone.RemainingCapacity"/> is a snapshot;
/// <paramref name="occupancy"/> can report a tighter live remaining capacity that also accounts for
/// in-flight put-away tasks already targeting a zone. This strategy therefore treats a zone as having
/// capacity only when BOTH the domain compatibility check and
/// <see cref="IZoneOccupancy.RemainingCapacity(ZoneId)"/> are strictly positive.
/// </para>
/// <para>
/// <b>Determinism (Req 16.2, 16.5).</b> Given identical inventory state and identical inputs the
/// candidate set and its ascending-<see cref="ZoneId"/> order are identical, so the same zone is
/// always chosen; ties are broken by ascending zone id. When no compatible zone with capacity exists
/// the result is <see cref="SlottingResult.Unslottable(DomainError)"/> carrying
/// <see cref="DomainError.Unslottable(string)"/> (Req 16.3). Raising the <c>BlockedPlacement</c> event
/// on an unslottable outcome is the put-away handler's responsibility (task 24.2), not this strategy's.
/// </para>
/// </summary>
public sealed class NaiveFirstAvailableStrategy : ISlottingStrategy
{
    /// <inheritdoc />
    public string Key => SlottingStrategyKey.NaiveFirstAvailable;

    /// <inheritdoc />
    public SlottingResult SelectZone(
        GelLot lot,
        GelType gelType,
        IReadOnlyCollection<TemperatureZone> zones,
        IZoneOccupancy occupancy)
    {
        ArgumentNullException.ThrowIfNull(lot);
        ArgumentNullException.ThrowIfNull(gelType);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(occupancy);

        // Domain decides compatibility (allowable range contains storage range) + snapshot capacity,
        // ordered ascending by zone id (Req 16.1, 16.2, 16.4).
        var candidates = SlottingCandidates.CompatibleZones(gelType, zones);

        // Take the first candidate that also has live remaining capacity per the occupancy view.
        // Candidates are already ascending by zone id, so the first qualifying one is the smallest id.
        foreach (var zone in candidates)
        {
            if (occupancy.RemainingCapacity(zone.Id) > 0)
            {
                return SlottingResult.Slotted(zone.Id);
            }
        }

        return SlottingResult.Unslottable(DomainError.Unslottable(
            "No compatible temperature zone with available capacity exists for the gel lot."));
    }
}
