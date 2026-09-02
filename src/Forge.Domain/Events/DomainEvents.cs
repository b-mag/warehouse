using Forge.Domain.Common;

namespace Forge.Domain.Events;

// Domain event records (Req 4.6, 6.3, 27.3, 27.4).
//
// These mirror the Forge.Contracts.Events schemas one-for-one, but carry DOMAIN types
// (strongly-typed ids) instead of raw Guids. They are raised by domain rules and mapped to
// the Contracts.Events records at the Application/Infrastructure boundary. The domain stays
// BCL-only: nothing here references Forge.Contracts.
//
// On OperatorParameterChanged: the Contracts schema carries an OperatorParameterStateDto and
// is published by the Application's UpdateOperatorParameterHandler after validating and
// applying a parameter to the live system (design "Core rule application" / Error Handling
// rows for Req 20.8). It is not produced by any pure domain rule and would require a Contracts
// DTO the BCL-only domain cannot reference, so it is intentionally an APPLICATION-layer event
// and is NOT modeled here.

/// <summary>
/// Raised on the non-expired to expired transition of a gel lot (Req 4.6, 27.4).
/// Mirrors <c>Contracts.Events.LotExpiredEvent(Guid LotId, DateTimeOffset At)</c>.
/// </summary>
public sealed record LotExpired(GelLotId LotId, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when a temperature reading falls outside a lot's allowable range (Req 6.3, 27.4).
/// Mirrors <c>Contracts.Events.TemperatureExcursionEvent(Guid LotId, decimal Celsius, DateTimeOffset At)</c>.
/// </summary>
public sealed record TemperatureExcursion(GelLotId LotId, decimal Celsius, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when an inbound arrival cannot be received, e.g. no dock slot (Req 27.4).
/// Mirrors <c>Contracts.Events.BlockedArrivalEvent(Guid LotId, string Reason)</c>.
/// </summary>
public sealed record BlockedArrival(GelLotId LotId, string Reason, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when a lot cannot be slotted into any compatible zone (Req 27.4).
/// Mirrors <c>Contracts.Events.BlockedPlacementEvent(Guid LotId, string Reason)</c>.
/// </summary>
public sealed record BlockedPlacement(GelLotId LotId, string Reason, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when no traversable path exists between a task's origin and destination (Req 27.4).
/// Mirrors <c>Contracts.Events.UnroutableTaskEvent(Guid TaskId, int Ox, int Oy, int Dx, int Dy)</c>,
/// carrying the origin/destination cell coordinates.
/// </summary>
public sealed record UnroutableTask(
    WarehouseTaskId TaskId,
    int OriginX,
    int OriginY,
    int DestinationX,
    int DestinationY,
    DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when a dock bay is occupied and a competing operation is queued (Req 27.4).
/// Mirrors <c>Contracts.Events.DockBlockedEvent(Guid DockBayId, DateTimeOffset At)</c>.
/// </summary>
public sealed record DockBlocked(DockBayId DockBayId, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised on loading-window close, reporting loaded quantity and shortfall (Req 27.4).
/// Mirrors <c>Contracts.Events.LoadingWindowClosedEvent(Guid StarshipId, int Loaded, int Shortfall)</c>.
/// </summary>
public sealed record LoadingWindowClosed(
    StarshipId StarshipId,
    int Loaded,
    int Shortfall,
    DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when a receiving/outbound backlog size changes (Req 27.4).
/// Mirrors <c>Contracts.Events.BacklogChangedEvent(string Kind, int NewSize)</c>.
/// </summary>
public sealed record BacklogChanged(string Kind, int NewSize, DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}

/// <summary>
/// Raised when a worker completes a warehouse task, recording the completion (Req 8.4, 8.5).
/// <para>
/// Req 8.4 requires that completing a task records the completion and publishes a domain event via
/// the Event_Bus, and Req 8.5 requires tasks be distributed through the Event_Bus. This event is the
/// completion notification the Application's <c>CompleteTaskHandler</c> (task 24.5) publishes through
/// <c>IEventBus</c> after the domain <see cref="Forge.Domain.Tasks.WarehouseTask.Complete"/> transition
/// succeeds. It carries the completed task's id, the worker credited with the completion, and the
/// <see cref="LaborCost"/> accrued for this task (Req 15.3) so subscribers can observe cost without
/// re-deriving it. There is no dedicated Contracts schema for this event yet; the boundary mapper can
/// map it when one is added. This record stays BCL-only, using strongly-typed domain ids like its peers.
/// </para>
/// </summary>
/// <param name="TaskId">The completed task's identity.</param>
/// <param name="WorkerId">The worker credited with completing the task (Req 8.4).</param>
/// <param name="LaborCost">The labor cost accrued for this task's completion (Req 15.3); always &gt;= 0.</param>
/// <param name="At">When the completion occurred, in simulated time.</param>
public sealed record TaskCompleted(
    WarehouseTaskId TaskId,
    WorkerId WorkerId,
    decimal LaborCost,
    DateTimeOffset At) : IDomainEvent
{
    public DateTimeOffset OccurredAt => At;
}
