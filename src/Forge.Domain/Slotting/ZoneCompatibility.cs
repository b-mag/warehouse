using Forge.Domain.ColdChain;
using Forge.Domain.Gels;

namespace Forge.Domain.Slotting;

/// <summary>
/// Strategy-agnostic slotting compatibility primitive (Req 16.1). A temperature zone is
/// <em>compatible</em> with a gel that must be stored within a given range exactly when:
/// <list type="number">
///   <item><description>
///     the zone's <see cref="TemperatureZone.AllowableRange"/> <b>contains</b> the required
///     storage range (<see cref="TemperatureRange.ContainsRange(TemperatureRange)"/>) — the
///     zone can hold the gel within its whole temperature band; and
///   </description></item>
///   <item><description>
///     the zone has <b>available capacity</b> — <see cref="TemperatureZone.RemainingCapacity"/>
///     is strictly positive.
///   </description></item>
/// </list>
/// <para>
/// This is a pure, deterministic predicate over the zone and the storage range only: identical
/// inputs always yield an identical outcome. It carries none of the strategy-specific preference
/// logic — velocity/affinity travel-time minimization belongs to
/// <c>VelocityAffinitySlottingStrategy</c> (Application task 18.1). Both Phase-1 strategies build
/// on this shared check so "compatible with capacity" means the same thing everywhere (Req 16.1,
/// 16.5, Property 9).
/// </para>
/// </summary>
public static class ZoneCompatibility
{
    /// <summary>
    /// True when <paramref name="zone"/> is compatible with a gel whose required storage band is
    /// <paramref name="storageRange"/> and has available capacity (Req 16.1). Namely, the zone's
    /// allowable range contains <paramref name="storageRange"/> and
    /// <see cref="TemperatureZone.RemainingCapacity"/> &gt; 0.
    /// </summary>
    /// <param name="zone">The candidate storage zone.</param>
    /// <param name="storageRange">The gel's required storage temperature band.</param>
    /// <exception cref="ArgumentNullException"><paramref name="zone"/> is null.</exception>
    public static bool IsCompatible(TemperatureZone zone, TemperatureRange storageRange)
    {
        ArgumentNullException.ThrowIfNull(zone);

        return zone.AllowableRange.ContainsRange(storageRange)
            && zone.RemainingCapacity > 0;
    }

    /// <summary>
    /// True when <paramref name="zone"/> is compatible with <paramref name="gelType"/> and has
    /// available capacity (Req 16.1). Convenience overload that reads the required storage band
    /// from the gel type's <see cref="Formulation.StorageRange"/>.
    /// </summary>
    /// <param name="zone">The candidate storage zone.</param>
    /// <param name="gelType">The gel type whose formulation storage range must be contained.</param>
    /// <exception cref="ArgumentNullException"><paramref name="zone"/> or <paramref name="gelType"/> is null.</exception>
    public static bool IsCompatible(TemperatureZone zone, GelType gelType)
    {
        ArgumentNullException.ThrowIfNull(gelType);

        return IsCompatible(zone, gelType.Formulation.StorageRange);
    }
}
