using Forge.Domain.Common;
using Forge.Domain.Docks;

namespace Forge.Application.Docks;

/// <summary>
/// An inbound or outbound operation that competes for a <see cref="DockBay"/> time slot
/// (Req 17.2, 17.3). The <see cref="DockScheduler"/> assigns it to its target bay when the bay is
/// free for its <see cref="Slot"/>, or queues it FIFO otherwise and assigns it later when a slot
/// frees.
/// <para>
/// An operation carries its own stable <see cref="Id"/> so the scheduler can recognise re-requests
/// idempotently and locate it in the occupied set or wait queue. <see cref="EnqueueSequence"/> is a
/// scheduler-assigned arrival stamp giving the wait queue a total, deterministic FIFO order
/// (Req 17.5); callers do not set it.
/// </para>
/// </summary>
public sealed class DockOperation
{
    /// <summary>
    /// Create a dock operation targeting <paramref name="bayId"/> for <paramref name="slot"/>.
    /// </summary>
    /// <param name="id">This operation's stable identity.</param>
    /// <param name="bayId">The bay the operation wants to use.</param>
    /// <param name="slot">The time slot (and direction) the operation needs on the bay.</param>
    public DockOperation(Guid id, DockBayId bayId, DockSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Id = id;
        BayId = bayId;
        Slot = slot;
    }

    /// <summary>This operation's stable identity, used to recognise re-requests and locate it.</summary>
    public Guid Id { get; }

    /// <summary>The bay this operation competes for (Req 17.2).</summary>
    public DockBayId BayId { get; }

    /// <summary>The slot — interval and direction (inbound/outbound) — the operation needs.</summary>
    public DockSlot Slot { get; }

    /// <summary>Convenience accessor for the slot's direction (Req 17.1/17.2).</summary>
    public DockOperationKind Kind => Slot.Kind;

    /// <summary>
    /// The scheduler-assigned arrival stamp establishing FIFO order in a bay's wait queue (Req 17.5).
    /// Set by <see cref="DockScheduler.Request"/> when the operation is queued; not caller-controlled.
    /// </summary>
    internal long EnqueueSequence { get; set; }
}

/// <summary>
/// The outcome of <see cref="DockScheduler.Request"/> (Req 17.2, 17.3). The operation was either
/// <see cref="IsAssigned">assigned</see> a slot immediately or queued at a FIFO position because the
/// bay was occupied for the requested slot.
/// </summary>
public sealed record DockRequestOutcome
{
    private DockRequestOutcome(bool isAssigned, DockOperation operation, int queuePosition)
    {
        IsAssigned = isAssigned;
        Operation = operation;
        QueuePosition = queuePosition;
    }

    /// <summary>True when the operation now occupies its slot; false when it was queued.</summary>
    public bool IsAssigned { get; }

    /// <summary>The operation the request was for.</summary>
    public DockOperation Operation { get; }

    /// <summary>
    /// When queued, the operation's zero-based position in the bay's FIFO wait queue; <c>-1</c> when
    /// assigned. Position 0 means it is next to be assigned when a slot frees (Req 17.5).
    /// </summary>
    public int QueuePosition { get; }

    internal static DockRequestOutcome Assigned(DockOperation operation) =>
        new(true, operation, -1);

    internal static DockRequestOutcome Queued(DockOperation operation, int position) =>
        new(false, operation, position);
}
