using Forge.Domain.Common;

namespace Forge.Domain.Docks;

/// <summary>
/// A single time slot on a <see cref="DockBay"/>'s <see cref="DockSchedule"/>, usable for
/// one direction of operation (Req 17.1). The half-open interval <c>[Start, End)</c> spans
/// the slot; <see cref="End"/> must be strictly after <see cref="Start"/>.
/// <para>
/// Construct through <see cref="Create"/> to enforce the ordering invariant; the primary
/// constructor is public so callers that already hold validated values (e.g. rehydration
/// from persistence) can build a slot directly, but they are responsible for upholding
/// <c>End &gt; Start</c>. The queue / assignment algorithm (Application task 20.1) is not
/// implemented here — this type only models a slot and offers pure interval queries.
/// </para>
/// </summary>
/// <param name="Start">The inclusive start of the slot in simulated time.</param>
/// <param name="End">The exclusive end of the slot; must be &gt; <paramref name="Start"/>.</param>
/// <param name="Kind">Whether the slot serves inbound receiving or outbound loading.</param>
public sealed record DockSlot(DateTimeOffset Start, DateTimeOffset End, DockOperationKind Kind)
{
    /// <summary>The slot's duration, always positive for a validly-constructed slot.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Create a slot, rejecting a non-positive interval (<c>End &lt;= Start</c>) with a
    /// <see cref="ErrorKind.Validation"/> error rather than throwing (Req 17.1).
    /// </summary>
    public static Result<DockSlot> Create(DateTimeOffset start, DateTimeOffset end, DockOperationKind kind)
    {
        if (end <= start)
        {
            return DomainError.Validation(
                "A dock slot's end must be strictly after its start.", nameof(end));
        }

        return new DockSlot(start, end, kind);
    }

    /// <summary>
    /// True when <paramref name="at"/> falls within this slot's half-open interval
    /// <c>[Start, End)</c>. Pure and deterministic; used by the scheduler to find the
    /// slot(s) active at an instant.
    /// </summary>
    public bool Contains(DateTimeOffset at) => at >= Start && at < End;

    /// <summary>
    /// True when this slot's interval overlaps <paramref name="other"/>'s. Two half-open
    /// intervals overlap when each starts strictly before the other ends. Adjacent slots
    /// (one ends exactly when the next starts) do not overlap. Pure and deterministic.
    /// </summary>
    public bool Overlaps(DockSlot other) => Start < other.End && other.Start < End;

    /// <summary>
    /// True when this slot's end time is at or before <paramref name="now"/>, i.e. the
    /// slot has already ended in simulated time. The scheduler treats an ended slot as
    /// unavailable (Req 17.6); the rejection itself is applied by the Application layer.
    /// </summary>
    public bool HasEnded(DateTimeOffset now) => End <= now;
}
