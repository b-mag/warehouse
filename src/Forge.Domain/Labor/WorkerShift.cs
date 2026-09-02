using Forge.Domain.Common;

namespace Forge.Domain.Labor;

/// <summary>
/// A scheduled interval during which a <see cref="Worker"/> is on shift (Req 15.1). A shift is a
/// value object defined by a <see cref="Start"/> and an <see cref="End"/> where the end is strictly
/// greater than the start (Req 15.1).
/// <para>
/// The type is an immutable <c>record</c> so two shifts compare equal when their start and end match,
/// and it is constructed only through the validated <see cref="Create(DateTimeOffset, DateTimeOffset)"/>
/// factory so an <c>End &lt;= Start</c> shift can never exist. The <see cref="Contains(DateTimeOffset)"/>
/// helper is a pure admission primitive using <b>inclusive</b> bounds: a moment exactly on the start or
/// exactly on the end is inside the shift. It mirrors <c>Vessels.LoadingWindow.IsOpenAt</c> so shift and
/// window admission behave identically.
/// </para>
/// <para>
/// This type provides the domain model plus the pure admission helper only. The
/// assign-only-when-on-shift flow (Req 15.5) that consumes it is an Application concern (task 19.1);
/// it is not implemented here.
/// </para>
/// </summary>
public sealed record WorkerShift
{
    private WorkerShift(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    /// <summary>The inclusive start of the shift (Req 15.1).</summary>
    public DateTimeOffset Start { get; }

    /// <summary>The inclusive end of the shift; strictly greater than <see cref="Start"/> (Req 15.1).</summary>
    public DateTimeOffset End { get; }

    /// <summary>
    /// Validated factory returning a <see cref="WorkerShift"/> on success or a typed error on rejection
    /// (Req 15.1). Rejects any shift whose <paramref name="end"/> is not strictly greater than its
    /// <paramref name="start"/> (i.e. <c>end &lt;= start</c>) with <see cref="DomainError.InvalidValue(string)"/>,
    /// leaving no shift constructed.
    /// </summary>
    /// <param name="start">The inclusive shift start.</param>
    /// <param name="end">The inclusive shift end; must be strictly greater than <paramref name="start"/>.</param>
    /// <returns>A successful <see cref="Result{WorkerShift}"/> when valid, otherwise a typed rejection.</returns>
    public static Result<WorkerShift> Create(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return DomainError.InvalidValue(
                $"Worker shift end must be strictly greater than start; got start {start:o}, end {end:o}.");
        }

        return new WorkerShift(start, end);
    }

    /// <summary>
    /// Pure admission helper: returns <c>true</c> when <paramref name="t"/> falls within this shift using
    /// <b>inclusive</b> bounds — <c>t &gt;= Start &amp;&amp; t &lt;= End</c>. Deterministic and side-effect free.
    /// </summary>
    /// <param name="t">The moment to test.</param>
    /// <returns><c>true</c> if <paramref name="t"/> is inside the shift inclusive of both ends.</returns>
    public bool Contains(DateTimeOffset t) => t >= Start && t <= End;
}
