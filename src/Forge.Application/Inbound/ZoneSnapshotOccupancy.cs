using Forge.Application.Abstractions.Slotting;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Application.Inbound;

/// <summary>
/// An <see cref="IZoneOccupancy"/> projected over a fixed set of <see cref="TemperatureZone"/>
/// snapshots (design "WMS Core Application abstractions"; Req 16.2). The put-away handler builds one
/// of these from the zones it fetched from <see cref="Forge.Application.Abstractions.Repositories.IZoneRepository"/>
/// so the active <see cref="Forge.Application.Abstractions.ISlottingStrategy"/> can read each zone's
/// live remaining capacity and effective occupancy without coupling the strategy to a repository or
/// the unit of work.
/// <para>
/// <b>Occupancy source.</b> Occupancy is a zone's own <see cref="TemperatureZone.StoredQuantity"/>
/// and remaining capacity its <see cref="TemperatureZone.RemainingCapacity"/> — the authoritative
/// values on the aggregate. A future refinement can layer in-flight put-away reservations on top
/// (the interface's intent) without changing this seam; for task 24.2 the zone snapshot is the
/// single source of truth, keeping the decision deterministic in the fetched state.
/// </para>
/// <para>An unknown zone reports zero remaining capacity and zero occupancy (it is not a candidate).</para>
/// </summary>
public sealed class ZoneSnapshotOccupancy : IZoneOccupancy
{
    private readonly Dictionary<ZoneId, TemperatureZone> _zones;

    /// <summary>
    /// Build an occupancy view over <paramref name="zones"/>. Null entries are ignored; a duplicate
    /// zone id keeps the first occurrence.
    /// </summary>
    public ZoneSnapshotOccupancy(IEnumerable<TemperatureZone> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);

        _zones = new Dictionary<ZoneId, TemperatureZone>();
        foreach (var zone in zones)
        {
            if (zone is not null)
            {
                _zones.TryAdd(zone.Id, zone);
            }
        }
    }

    /// <inheritdoc />
    public int RemainingCapacity(ZoneId zone) =>
        _zones.TryGetValue(zone, out var z) ? z.RemainingCapacity : 0;

    /// <inheritdoc />
    public int Occupancy(ZoneId zone) =>
        _zones.TryGetValue(zone, out var z) ? z.StoredQuantity : 0;
}
