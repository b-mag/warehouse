using Forge.Domain.Common;

namespace Forge.Domain.Vessels;

/// <summary>
/// A scheduled interval during which a <see cref="Starship"/> may be loaded (Req 13.1). A window
/// is a value object defined by a <see cref="Start"/> and an <see cref="End"/> where the end is
/// strictly greater than the start (Req 13.1).
/// <para>
/// The type is an immutable <c>record</c> so two windows compare equal when their start and end
/// match, and it is constructed only through the validated <see cref="Create(DateTimeOffset, DateTimeOffset)"/>
/// factory so an <c>End &lt;= Start</c> window can never exist. The <see cref="IsOpenAt(DateTimeOffset)"/>
/// helper is a pure admission primitive using <b>inclusive</b> bounds (Req 13.2): a moment exactly on
/// the start or exactly on the end is inside the window.
/// </para>
/// <para>
/// This type provides the domain model plus the pure admission helper only. The
/// reject-load-when-outside-window flow and the window-close shortfall <c>LoadingWindowClosed</c>
/// event are Application concerns (task 21.1) that consume this model; they are not implemented here.
/// </para>
/// </summary>
public sealed record LoadingWindow
{
    private LoadingWindow(DateTimeOffset start, DateTimeOffset end)
    {
        Start = start;
        End = end;
    }

    /// <summary>The inclusive start of the loading window (Req 13.1).</summary>
    public DateTimeOffset Start { get; }

    /// <summary>The inclusive end of the loading window; strictly greater than <see cref="Start"/> (Req 13.1).</summary>
    public DateTimeOffset End { get; }

    /// <summary>
    /// Validated factory returning a <see cref="LoadingWindow"/> on success or a typed error on
    /// rejection (Req 13.1). Rejects any window whose <paramref name="end"/> is not strictly greater
    /// than its <paramref name="start"/> (i.e. <c>end &lt;= start</c>) with
    /// <see cref="DomainError.InvalidValue(string)"/>, leaving no window constructed.
    /// </summary>
    /// <param name="start">The inclusive window start.</param>
    /// <param name="end">The inclusive window end; must be strictly greater than <paramref name="start"/>.</param>
    /// <returns>A successful <see cref="Result{LoadingWindow}"/> when valid, otherwise a typed rejection.</returns>
    public static Result<LoadingWindow> Create(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return DomainError.InvalidValue(
                $"Loading window end must be strictly greater than start; got start {start:o}, end {end:o}.");
        }

        return new LoadingWindow(start, end);
    }

    /// <summary>
    /// Pure admission helper: returns <c>true</c> when <paramref name="t"/> falls within this window
    /// using <b>inclusive</b> bounds — <c>t &gt;= Start &amp;&amp; t &lt;= End</c> (Req 13.2). Deterministic
    /// and side-effect free.
    /// </summary>
    /// <param name="t">The moment to test.</param>
    /// <returns><c>true</c> if <paramref name="t"/> is inside the window inclusive of both ends.</returns>
    public bool IsOpenAt(DateTimeOffset t) => t >= Start && t <= End;
}
