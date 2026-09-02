using Forge.Domain.Common;

namespace Forge.Domain.Docks;

/// <summary>
/// A dock bay: a constrained resource shared by inbound receiving and outbound loading
/// operations (Req 17.1). A bay holds a <see cref="DockSchedule"/> of assigned time slots
/// and an <see cref="IsOpen"/> flag reflecting the operator's open-dock-bays parameter.
/// <para>
/// A dock bay is also a single-occupancy resource: at most one agent uses it at a time
/// (Req 19.4), so it implements <see cref="ISingleOccupancyResource"/> to give the
/// reservation manager (task 15.2) a key to acquire and queue on. The scheduling /
/// contention algorithm (Application task 20.1) — queueing competing operations,
/// earliest-queued assignment, utilization — lives outside the domain; this class only
/// models the bay's state and identity.
/// </para>
/// </summary>
public sealed class DockBay : ISingleOccupancyResource
{
    /// <summary>
    /// Create a dock bay. A <see langword="null"/> <paramref name="schedule"/> defaults to
    /// an empty schedule.
    /// </summary>
    public DockBay(DockBayId id, bool isOpen, DockSchedule? schedule = null)
    {
        Id = id;
        IsOpen = isOpen;
        Schedule = schedule ?? DockSchedule.Empty;
    }

    /// <summary>This bay's identity (Req 17.1).</summary>
    public DockBayId Id { get; }

    /// <summary>
    /// Whether the bay is open for operations. A closed bay is not usable even if its
    /// schedule has slots; the operator controls how many bays are open (Req 17, 20).
    /// </summary>
    public bool IsOpen { get; }

    /// <summary>The bay's assigned time slots (Req 17.1).</summary>
    public DockSchedule Schedule { get; }

    /// <inheritdoc />
    public SingleOccupancyResourceId ResourceId => SingleOccupancyResourceId.ForDockBay(Id);
}
