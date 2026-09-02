using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Slotting;

/// <summary>
/// A small read-only abstraction exposing the per-zone occupancy a slotting strategy needs to make
/// a placement decision (design "WMS Core Application abstractions"; Req 16.2). It lets a strategy
/// consider live remaining capacity beyond a zone's own snapshot — e.g. accounting for in-flight
/// put-away tasks already targeting a zone — without coupling the strategy to a repository or the
/// unit of work. The concrete implementation is provided where slotting runs (task 18.1 / 24.2).
/// </summary>
public interface IZoneOccupancy
{
    /// <summary>The remaining available capacity for the given zone (capacity minus effective occupancy).</summary>
    int RemainingCapacity(ZoneId zone);

    /// <summary>The current effective occupancy (stored + reserved in-flight) for the given zone.</summary>
    int Occupancy(ZoneId zone);
}
