using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Slotting;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Slotting;

namespace Forge.Application.Slotting;

/// <summary>
/// The default Phase-1 "velocity/affinity" slotting strategy (Req 16, 20.7): among the zones whose
/// allowable range contains the gel type's storage range and that have available capacity, it prefers
/// the zone that minimizes the expected future <c>Travel_Time</c> for higher-<see cref="GelType.Velocity"/>
/// gels — fast movers are placed in the more accessible (less congested) compatible zones (Req 16.1).
/// It builds on the strategy-agnostic domain slotting primitives
/// (<see cref="SlottingCandidates.CompatibleZones(TemperatureRange, System.Collections.Generic.IEnumerable{TemperatureZone})"/>),
/// so compatibility + capacity are decided in the Engine and slotting stays in the WMS Core (Req 16.4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Heuristic (documented, deterministic — to be refined).</b> A full <c>Travel_Time</c>-to-dock/pick-face
/// model does not yet exist at the Application layer, so this strategy uses a documented deterministic
/// <em>accessibility proxy</em> derived from <see cref="IZoneOccupancy"/>:
/// <list type="bullet">
///   <item><description>
///     A zone's <b>accessibility cost</b> is its effective occupancy — <see cref="IZoneOccupancy.Occupancy(ZoneId)"/>.
///     A fuller zone is treated as less accessible: it is more congested, its nearest free pick faces are
///     deeper, and expected future travel time to reach a slot in it is higher. An emptier zone is more
///     accessible (a nearer free slot, lower expected travel time).
///   </description></item>
///   <item><description>
///     For higher-<see cref="GelType.Velocity"/> gels this accessibility cost is weighted more heavily, so
///     fast movers are pulled toward the most accessible compatible zones while slow movers are largely
///     indifferent to accessibility. Concretely the ranking key is
///     <c>(1 + Velocity) × Occupancy(zone)</c>: as velocity rises, differences in occupancy matter more.
///   </description></item>
///   <item><description>
///     The selected zone <b>minimizes</b> that velocity-weighted accessibility cost. Ties (including the
///     velocity-0 case, where every compatible zone has cost 0) are broken by <b>ascending
///     <see cref="ZoneId"/></b> via <see cref="ZoneIdComparer"/> (Req 16.2).
///   </description></item>
/// </list>
/// This proxy is fully deterministic in (inventory state, inputs): identical occupancy + zones + gel type
/// always produce an identical selection (Req 16.5). It will be refined to a true expected-travel-time term
/// once travel-time-to-dock is modeled (grid distance from zone to dock/pick face); the ranking shape
/// (velocity-weighted accessibility cost, ascending-id tie-break) is designed to accommodate that swap
/// without changing the seam.
/// </para>
/// <para>
/// <b>Unslottable.</b> When no compatible zone with capacity exists the result is
/// <see cref="SlottingResult.Unslottable(DomainError)"/> carrying <see cref="DomainError.Unslottable(string)"/>
/// (Req 16.3). Raising the <c>BlockedPlacement</c> event is the put-away handler's job (task 24.2), not here.
/// </para>
/// </remarks>
public sealed class VelocityAffinitySlottingStrategy : ISlottingStrategy
{
    /// <inheritdoc />
    public string Key => SlottingStrategyKey.VelocityAffinity;

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

        // Higher velocity => accessibility (occupancy) differences weigh more. See remarks.
        var velocityWeight = 1.0 + gelType.Velocity;

        TemperatureZone? best = null;
        var bestCost = double.PositiveInfinity;

        foreach (var zone in candidates)
        {
            // A zone must also have live remaining capacity per the occupancy view, not just the
            // snapshot the domain compatibility check saw.
            if (occupancy.RemainingCapacity(zone.Id) <= 0)
            {
                continue;
            }

            // Accessibility proxy: a fuller zone is less accessible (higher expected travel time),
            // weighted by gel velocity so fast movers prefer the most accessible compatible zone.
            var cost = velocityWeight * occupancy.Occupancy(zone.Id);

            // Strictly-less keeps the first (smallest-id) zone on a tie because candidates are already
            // in ascending zone-id order — so ties break by ascending zone id (Req 16.2).
            if (cost < bestCost)
            {
                bestCost = cost;
                best = zone;
            }
        }

        if (best is null)
        {
            return SlottingResult.Unslottable(DomainError.Unslottable(
                "No compatible temperature zone with available capacity exists for the gel lot."));
        }

        return SlottingResult.Slotted(best.Id);
    }
}
