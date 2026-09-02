namespace Forge.Domain.Docks;

/// <summary>
/// The set of assigned time slots for a <see cref="DockBay"/> (Req 17.1). A schedule is an
/// immutable, ordered view over its <see cref="Slots"/>; mutating a schedule produces a new
/// one so identical inputs always yield identical query results (determinism).
/// <para>
/// This type carries only <em>pure</em>, deterministic queries the scheduler (Application
/// task 20.1) consumes — slots active at an instant, the next slot to free, whether an
/// interval is free. It deliberately does <b>not</b> implement queueing, earliest-queued
/// assignment, or utilization accounting; those are the Application scheduling algorithm.
/// </para>
/// </summary>
public sealed class DockSchedule
{
    private readonly DockSlot[] _slots;

    /// <summary>
    /// Build a schedule from the given slots. Slots are stored in a stable deterministic
    /// order — ascending by <see cref="DockSlot.Start"/>, then <see cref="DockSlot.End"/>,
    /// then <see cref="DockSlot.Kind"/> — so query results never depend on input ordering.
    /// </summary>
    public DockSchedule(IEnumerable<DockSlot> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        _slots = slots
            .OrderBy(s => s.Start)
            .ThenBy(s => s.End)
            .ThenBy(s => (int)s.Kind)
            .ToArray();
    }

    /// <summary>An empty schedule with no assigned slots.</summary>
    public static DockSchedule Empty { get; } = new(Array.Empty<DockSlot>());

    /// <summary>The assigned slots, ordered ascending by start then end then kind.</summary>
    public IReadOnlyList<DockSlot> Slots => _slots;

    /// <summary>The number of scheduled slots.</summary>
    public int Count => _slots.Length;

    /// <summary>
    /// The slots whose half-open interval contains <paramref name="at"/> (Req 17.2 support).
    /// Pure and deterministic; returned in schedule order.
    /// </summary>
    public IReadOnlyList<DockSlot> SlotsAt(DateTimeOffset at)
    {
        var matches = new List<DockSlot>();
        foreach (var slot in _slots)
        {
            if (slot.Contains(at))
            {
                matches.Add(slot);
            }
        }

        return matches;
    }

    /// <summary>
    /// True when no scheduled slot overlaps the interval <c>[start, end)</c>. Callers use
    /// this to check whether a candidate operation could occupy an interval without
    /// colliding with an existing assignment. Pure and deterministic. An interval with
    /// <c>end &lt;= start</c> is treated as free (it occupies no time).
    /// </summary>
    public bool IsFree(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return true;
        }

        var probe = new DockSlot(start, end, DockOperationKind.Inbound);
        foreach (var slot in _slots)
        {
            if (slot.Overlaps(probe))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The earliest slot that ends strictly after <paramref name="after"/> — i.e. the next
    /// slot that is still or not-yet occupied relative to that instant. Returns
    /// <see langword="null"/> when every scheduled slot has already ended. This is a pure
    /// lookup the scheduler uses to decide when a bay next frees; it does not assign anyone
    /// to the slot. Pure and deterministic.
    /// </summary>
    public DockSlot? NextSlotEndingAfter(DateTimeOffset after)
    {
        DockSlot? next = null;
        foreach (var slot in _slots)
        {
            if (slot.End <= after)
            {
                continue;
            }

            if (next is null || slot.End < next.End)
            {
                next = slot;
            }
        }

        return next;
    }

    /// <summary>
    /// A new schedule that also contains <paramref name="slot"/>. The original is unchanged
    /// (schedules are immutable), keeping queries deterministic across a bay's lifetime.
    /// </summary>
    public DockSchedule With(DockSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return new DockSchedule(_slots.Append(slot));
    }
}
