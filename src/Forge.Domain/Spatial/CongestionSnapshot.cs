namespace Forge.Domain.Spatial;

/// <summary>
/// A domain-pure, point-in-time projection of reservation congestion (Req 19.5), derived from the
/// currently held path-segment reservations and the agents queued on single-occupancy resources.
/// The Application's reservation manager (task 17.1) builds this from the
/// <see cref="ReservationLedger"/> and single-occupancy registry and maps it to the transport
/// <c>CongestionDto</c>; congestion exposure itself is task 22.1.
/// </summary>
/// <param name="ReservedSegments">
/// The number of distinct path segments that currently hold at least one reservation — how much of
/// the grid is spoken for.
/// </param>
/// <param name="QueuedAgents">
/// The total number of agents currently waiting in single-occupancy FIFO queues — how much
/// contention is backed up behind held resources.
/// </param>
/// <param name="HotCells">
/// Cells experiencing the most contention (e.g. endpoints of the most-reserved segments), ordered
/// deterministically so identical state yields an identical snapshot. May be empty.
/// </param>
public sealed record CongestionSnapshot(
    int ReservedSegments,
    int QueuedAgents,
    IReadOnlyList<Cell> HotCells)
{
    /// <summary>A snapshot with no reserved segments, no queued agents, and no hot cells.</summary>
    public static CongestionSnapshot Empty { get; } =
        new(0, 0, Array.Empty<Cell>());
}
