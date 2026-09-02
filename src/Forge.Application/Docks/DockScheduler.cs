using Forge.Domain.Common;
using Forge.Domain.Docks;

namespace Forge.Application.Docks;

/// <summary>
/// Schedules inbound and outbound operations onto <see cref="DockBay"/> time slots, controlling
/// dock contention and exposing it observably (Req 17.2–17.6).
/// <para>
/// A dock bay is a constrained resource shared by inbound receiving and outbound loading. While a
/// bay is occupied for a time slot the scheduler rejects a competing assignment for that slot and
/// queues the competing operation (Req 17.2). When inbound and outbound both need a bay and none is
/// free the operations are queued and the contention is exposed as a non-negative backlog count
/// (Req 17.3). Utilization is the fraction of scheduled slots currently occupied (Req 17.4). When a
/// slot frees the earliest queued operation waiting for that bay is assigned, deterministically by
/// arrival order (Req 17.5). A request whose slot has already ended in simulated time is rejected
/// with <see cref="ErrorKind.SlotUnavailable"/> (Req 17.6).
/// </para>
/// <para>
/// <b>Why a dock-specific scheduler and not <see cref="SingleOccupancyRegistry"/>.</b> The domain's
/// <c>SingleOccupancyRegistry</c> grants a resource to at most one <see cref="AgentId"/> and queues
/// the rest FIFO — it models "who holds the bay", keyed only by agent, with no notion of time slots,
/// operation direction, utilization, or past-slot rejection. Dock scheduling instead reasons about
/// per-bay <see cref="DockSlot"/> intervals (each dedicated to inbound or outbound), overlapping-slot
/// contention, occupied-vs-scheduled utilization, and simulated-time expiry — none of which the
/// registry represents. Rather than bolt time-slot semantics onto an agent-keyed grant table, this
/// scheduler manages dock operations directly while deliberately reusing the registry's proven
/// discipline: one occupant at a time and a strict FIFO (earliest-queued) wait queue granted on
/// release. That keeps the two concerns cleanly separated and keeps this class the single home of
/// Req 17's scheduling algorithm, as the domain <c>DockBay</c>/<c>DockSchedule</c> XML docs anticipate.
/// </para>
/// </summary>
public sealed class DockScheduler
{
    private sealed class BayState
    {
        public required DockBay Bay { get; set; }

        /// <summary>Occupied slots keyed by the operation currently holding each.</summary>
        public readonly List<DockAssignment> Occupied = new();

        /// <summary>Operations waiting for this bay, in strict arrival (FIFO) order.</summary>
        public readonly List<DockOperation> Queue = new();
    }

    private readonly Dictionary<DockBayId, BayState> _bays = new();

    /// <summary>
    /// A monotonic counter stamped onto each queued operation so the wait queue has a total,
    /// deterministic order even when two operations arrive at the same simulated instant. This is
    /// the earliest-queued (FIFO) discipline Req 17.5 requires.
    /// </summary>
    private long _sequence;

    /// <summary>
    /// Register a bay so operations can be scheduled onto it. Re-registering the same
    /// <see cref="DockBay.Id"/> updates the bay reference (e.g. its open flag) while preserving any
    /// existing occupancy and queue.
    /// </summary>
    public void RegisterBay(DockBay bay)
    {
        ArgumentNullException.ThrowIfNull(bay);

        if (_bays.TryGetValue(bay.Id, out var state))
        {
            state.Bay = bay;
        }
        else
        {
            _bays[bay.Id] = new BayState { Bay = bay };
        }
    }

    /// <summary>
    /// Request assignment of <paramref name="operation"/> to its target bay for its slot at simulated
    /// time <paramref name="now"/> (Req 17.2, 17.3, 17.6).
    /// <list type="bullet">
    /// <item>Rejects with <see cref="ErrorKind.SlotUnavailable"/> when the slot has already ended in
    /// simulated time (Req 17.6), when the bay is unknown, or when the bay is closed.</item>
    /// <item>Assigns the operation when the bay has no occupied slot overlapping the requested slot
    /// (the bay is free for that interval).</item>
    /// <item>Otherwise queues the operation FIFO and reports it as queued — this is the competing
    /// inbound/outbound contention of Req 17.2/17.3, surfaced through <see cref="Backlog"/>.</item>
    /// </list>
    /// State is left unchanged on rejection.
    /// </summary>
    public Result<DockRequestOutcome> Request(DockOperation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.Slot.HasEnded(now))
        {
            return DomainError.SlotUnavailable(
                "The requested dock slot has already ended in simulated time.");
        }

        if (!_bays.TryGetValue(operation.BayId, out var state))
        {
            return DomainError.SlotUnavailable(
                $"Dock bay {operation.BayId} is not registered with the scheduler.");
        }

        if (!state.Bay.IsOpen)
        {
            return DomainError.SlotUnavailable($"Dock bay {operation.BayId} is closed.");
        }

        // Already assigned or already queued? Idempotent — report current standing without duplicating.
        if (state.Occupied.Any(a => a.Operation.Id.Equals(operation.Id)))
        {
            return DockRequestOutcome.Assigned(operation);
        }

        int queuedIndex = state.Queue.FindIndex(o => o.Id.Equals(operation.Id));
        if (queuedIndex >= 0)
        {
            return DockRequestOutcome.Queued(operation, queuedIndex);
        }

        if (IsFreeForSlot(state, operation.Slot))
        {
            state.Occupied.Add(new DockAssignment(operation, operation.Slot));
            return DockRequestOutcome.Assigned(operation);
        }

        // Competing operation while the bay is occupied for the slot (Req 17.2): queue it FIFO.
        operation.EnqueueSequence = _sequence++;
        state.Queue.Add(operation);
        return DockRequestOutcome.Queued(operation, state.Queue.Count - 1);
    }

    /// <summary>
    /// Free the slot held by <paramref name="operation"/> on its bay and assign the earliest queued
    /// operation that can now use the bay (Req 17.5). Returns the newly-assigned operation, or
    /// <see langword="null"/> when nothing was waiting that could be assigned. Assignment order is a
    /// deterministic function of queue arrival order, so identical queue state yields an identical
    /// assignment.
    /// </summary>
    public DockOperation? Release(DockOperation operation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!_bays.TryGetValue(operation.BayId, out var state))
        {
            return null;
        }

        int held = state.Occupied.FindIndex(a => a.Operation.Id.Equals(operation.Id));
        if (held >= 0)
        {
            state.Occupied.RemoveAt(held);
        }

        return AssignEarliestQueued(state, now);
    }

    /// <summary>
    /// Assign the earliest queued operation whose slot the bay is now free for, skipping any queued
    /// operation whose slot has already ended in simulated time (dropping it as unschedulable).
    /// Deterministic: the queue is scanned in strict FIFO (arrival) order (Req 17.5).
    /// </summary>
    private DockOperation? AssignEarliestQueued(BayState state, DateTimeOffset now)
    {
        // Drop expired queued operations first so a stale head never blocks a live waiter (Req 17.6).
        state.Queue.RemoveAll(o => o.Slot.HasEnded(now));

        for (int i = 0; i < state.Queue.Count; i++)
        {
            var candidate = state.Queue[i];
            if (IsFreeForSlot(state, candidate.Slot))
            {
                state.Queue.RemoveAt(i);
                state.Occupied.Add(new DockAssignment(candidate, candidate.Slot));
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The number of operations queued for <paramref name="bayId"/> (Req 17.3), always ≥ 0.</summary>
    public int BacklogFor(DockBayId bayId) =>
        _bays.TryGetValue(bayId, out var state) ? state.Queue.Count : 0;

    /// <summary>
    /// The total dock contention backlog across all bays (Req 17.3): the count of operations queued
    /// because no bay slot was free. Always a non-negative integer.
    /// </summary>
    public int Backlog => _bays.Values.Sum(s => s.Queue.Count);

    /// <summary>
    /// The operations currently occupying a slot on <paramref name="bayId"/>, in assignment order.
    /// Empty when the bay is free or unknown.
    /// </summary>
    public IReadOnlyList<DockOperation> OccupantsOf(DockBayId bayId) =>
        _bays.TryGetValue(bayId, out var state)
            ? state.Occupied.Select(a => a.Operation).ToArray()
            : Array.Empty<DockOperation>();

    /// <summary>
    /// The operations queued for <paramref name="bayId"/> in FIFO (arrival) order (Req 17.5 ordering).
    /// Empty when nothing waits or the bay is unknown.
    /// </summary>
    public IReadOnlyList<DockOperation> QueueOf(DockBayId bayId) =>
        _bays.TryGetValue(bayId, out var state)
            ? state.Queue.ToArray()
            : Array.Empty<DockOperation>();

    /// <summary>
    /// The utilization of <paramref name="bayId"/> as the fraction of its scheduled slots that are
    /// currently occupied (Req 17.4): <c>occupied / scheduled</c>, in <c>[0, 1]</c>. A bay with no
    /// scheduled slots has utilization <c>0</c>. Deterministic given identical bay state.
    /// </summary>
    public double UtilizationOf(DockBayId bayId)
    {
        if (!_bays.TryGetValue(bayId, out var state))
        {
            return 0d;
        }

        int scheduled = state.Bay.Schedule.Count;
        if (scheduled == 0)
        {
            return 0d;
        }

        // Occupancy is capped at the scheduled count so utilization never exceeds 1.
        int occupied = Math.Min(state.Occupied.Count, scheduled);
        return (double)occupied / scheduled;
    }

    /// <summary>True when no occupied slot on the bay overlaps <paramref name="slot"/>'s interval.</summary>
    private static bool IsFreeForSlot(BayState state, DockSlot slot)
    {
        foreach (var assignment in state.Occupied)
        {
            if (assignment.Slot.Overlaps(slot))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct DockAssignment(DockOperation Operation, DockSlot Slot);
}
