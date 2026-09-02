using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Spatial;

namespace Forge.Application.Abstractions;

/// <summary>
/// The reservation seam guaranteeing path-segment mutual-exclusion and single-occupancy of dock
/// bays / pick faces (design "WMS Core Application abstractions"; Req 19). The concrete
/// reservation ledger + single-occupancy manager live in <c>Forge.Domain.Spatial</c> (task 15);
/// this abstraction lets DI expose them to the core's per-tick movement stage.
/// <para>
/// The outcome types <see cref="ReservationOutcome"/>, <see cref="ResourceOutcome"/>, and
/// <see cref="CongestionSnapshot"/> are owned by the domain reservation subsystem
/// (<c>Forge.Domain.Spatial</c>, task 15). <see cref="TimedSegment"/> lives in
/// <c>Forge.Domain.Spatial</c> and <see cref="SingleOccupancyResourceId"/> in
/// <c>Forge.Domain.Docks</c>.
/// </para>
/// </summary>
public interface IReservationManager
{
    /// <summary>
    /// Attempt to reserve the given timed segments for <paramref name="agent"/>, enforcing the
    /// single grant point — any overlapping interval on a segment is rejected, contention resolved
    /// deterministically with the lower <see cref="AgentId"/> winning (Req 19.1, 19.2, 19.3, 19.6).
    /// </summary>
    ReservationOutcome TryReserve(AgentId agent, IReadOnlyList<TimedSegment> segments);

    /// <summary>
    /// Attempt to acquire a single-occupancy resource (dock bay / pick face) for
    /// <paramref name="agent"/>; grant to at most one agent and queue others FIFO (Req 19.4).
    /// </summary>
    ResourceOutcome TryAcquire(AgentId agent, SingleOccupancyResourceId resource);

    /// <summary>Release all reservations and resource grants held by <paramref name="agent"/>.</summary>
    void Release(AgentId agent);

    /// <summary>Expose current congestion derived from held reservations + queued agents (Req 19.5).</summary>
    CongestionSnapshot GetCongestion();
}
