namespace Forge.Domain.Docks;

/// <summary>
/// Whether a <see cref="DockSlot"/> on a <see cref="DockBay"/> is usable for an
/// inbound receiving operation or an outbound loading operation (Req 17.1).
/// <para>
/// A dock bay is a constrained resource shared by inbound and outbound work; each
/// scheduled slot is dedicated to one direction of operation. The scheduling /
/// contention algorithm (Application task 20.1) reads this to decide which queued
/// operation a freed slot can serve.
/// </para>
/// </summary>
public enum DockOperationKind
{
    /// <summary>An inbound receiving operation (unloading arriving gel at the dock).</summary>
    Inbound = 0,

    /// <summary>An outbound loading operation (loading gel onto a departing vessel).</summary>
    Outbound = 1,
}
