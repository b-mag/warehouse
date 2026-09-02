using Forge.Domain.Common;

namespace Forge.Domain.Spatial;

/// <summary>
/// A single held reservation: the <see cref="AgentId"/> that owns a
/// <see cref="TimedSegment"/> occupancy in the <see cref="ReservationLedger"/>
/// (Req 19.1, 19.3). One agent may hold many reservations; the ledger guarantees
/// no two <em>different</em> agents hold overlapping reservations on the same segment.
/// </summary>
public sealed record PathReservation(AgentId Agent, TimedSegment Timed);
