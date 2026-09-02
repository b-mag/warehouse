namespace Forge.Domain.Events;

/// <summary>
/// Marker interface for events raised by domain rules (Req 27.3, 27.4).
/// <para>
/// Domain rules (expiry decay, excursion detection, capacity/placement, routing, loading,
/// backlog) raise these pure records as they run. The Application's <c>IEventBus</c> publishes
/// <see cref="IDomainEvent"/>, and a boundary mapper translates each into the corresponding
/// <c>Forge.Contracts.Events</c> record (raw Guids) for transport. The domain never references
/// Contracts, so these events use strongly-typed domain ids instead of Guids.
/// </para>
/// </summary>
public interface IDomainEvent
{
    /// <summary>When the event occurred, in simulated time.</summary>
    DateTimeOffset OccurredAt { get; }
}
