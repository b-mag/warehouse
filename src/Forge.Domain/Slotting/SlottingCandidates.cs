using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Domain.Slotting;

/// <summary>
/// Strategy-agnostic candidate-selection primitives shared by every slotting strategy
/// (Req 16.1, 16.2, 16.3). This is the deterministic core the two Phase-1 strategies
/// (<c>VelocityAffinitySlottingStrategy</c> and <c>NaiveFirstAvailableStrategy</c>, Application
/// task 18.1) build on; it deliberately contains no velocity/affinity travel-time preference —
/// that lives in the velocity-affinity strategy only.
/// <para>
/// <b>Unslottable representation.</b> "No compatible zone with capacity exists" is reported by
/// reusing the existing <see cref="Result{T}"/> of <see cref="ZoneId"/> carrying
/// <see cref="DomainError.Unslottable(string)"/> (<see cref="ErrorKind.Unslottable"/>). This
/// reuses the domain's established typed-error result rather than introducing a new bespoke
/// outcome type, keeping every rejectable domain operation on the one <c>Result</c> contract
/// (Req 16.3). Raising the <c>BlockedPlacement</c> domain event is the responsibility of the
/// strategy / put-away handler, not this pure primitive.
/// </para>
/// </summary>
public static class SlottingCandidates
{
    /// <summary>
    /// Returns the zones from <paramref name="zones"/> that are compatible with
    /// <paramref name="storageRange"/> and have available capacity, ordered by ascending
    /// <see cref="TemperatureZone.Id"/> (Req 16.1, 16.2). The ascending-id order makes the result
    /// deterministic for identical inputs (Req 16.5) and gives strategies a stable tie-break basis.
    /// Null entries in <paramref name="zones"/> are ignored.
    /// </summary>
    /// <param name="storageRange">The gel's required storage temperature band.</param>
    /// <param name="zones">The zones to consider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="zones"/> is null.</exception>
    public static IReadOnlyList<TemperatureZone> CompatibleZones(
        TemperatureRange storageRange,
        IEnumerable<TemperatureZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        var candidates = new List<TemperatureZone>();
        foreach (var zone in zones)
        {
            if (zone is not null && ZoneCompatibility.IsCompatible(zone, storageRange))
            {
                candidates.Add(zone);
            }
        }

        candidates.Sort(ZoneIdComparer.Instance);
        return candidates;
    }

    /// <summary>
    /// Convenience overload of <see cref="CompatibleZones(TemperatureRange, IEnumerable{TemperatureZone})"/>
    /// reading the required storage band from <paramref name="gelType"/>'s
    /// <see cref="Formulation.StorageRange"/> (Req 16.1, 16.2).
    /// </summary>
    /// <param name="gelType">The gel type whose formulation storage range must be contained.</param>
    /// <param name="zones">The zones to consider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gelType"/> or <paramref name="zones"/> is null.</exception>
    public static IReadOnlyList<TemperatureZone> CompatibleZones(
        GelType gelType,
        IEnumerable<TemperatureZone> zones)
    {
        ArgumentNullException.ThrowIfNull(gelType);

        return CompatibleZones(gelType.Formulation.StorageRange, zones);
    }

    /// <summary>
    /// The neutral first-available selection over <paramref name="zones"/>: the compatible zone
    /// with capacity whose <see cref="TemperatureZone.Id"/> is smallest (Req 16.1, 16.2), or an
    /// <see cref="DomainError.Unslottable(string)"/> failure when none qualify (Req 16.3). This is
    /// the deterministic zone-id ordering primitive the strategies build on — the velocity-affinity
    /// strategy layers its travel-time preference on top and uses this only as a tie-break — so
    /// identical state and inputs yield an identical selection (Req 16.5, Property 9).
    /// </summary>
    /// <param name="storageRange">The gel's required storage temperature band.</param>
    /// <param name="zones">The zones to consider.</param>
    /// <returns>The selected <see cref="ZoneId"/>, or an unslottable failure when no candidate exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="zones"/> is null.</exception>
    public static Result<ZoneId> SelectByZoneId(
        TemperatureRange storageRange,
        IEnumerable<TemperatureZone> zones)
    {
        var candidates = CompatibleZones(storageRange, zones);
        if (candidates.Count == 0)
        {
            return DomainError.Unslottable(
                "No compatible temperature zone with available capacity exists for the gel.");
        }

        return candidates[0].Id;
    }

    /// <summary>
    /// Convenience overload of <see cref="SelectByZoneId(TemperatureRange, IEnumerable{TemperatureZone})"/>
    /// reading the required storage band from <paramref name="gelType"/>'s
    /// <see cref="Formulation.StorageRange"/> (Req 16.1, 16.2, 16.3).
    /// </summary>
    /// <param name="gelType">The gel type whose formulation storage range must be contained.</param>
    /// <param name="zones">The zones to consider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gelType"/> or <paramref name="zones"/> is null.</exception>
    public static Result<ZoneId> SelectByZoneId(
        GelType gelType,
        IEnumerable<TemperatureZone> zones)
    {
        ArgumentNullException.ThrowIfNull(gelType);

        return SelectByZoneId(gelType.Formulation.StorageRange, zones);
    }
}
