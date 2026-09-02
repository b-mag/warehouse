namespace Forge.Domain.Spatial;

/// <summary>
/// A <see cref="PathSegment"/> an agent occupies over the half-open simulated-time
/// interval <c>[EnterAt, ExitAt)</c> (Req 19.1). The reservation ledger (task 15.1)
/// keys mutual-exclusion on the segment and tests interval overlap on this type.
/// </summary>
/// <remarks>
/// The interval is <b>half-open</b>: <see cref="EnterAt"/> is inclusive and
/// <see cref="ExitAt"/> is exclusive. This means an agent that exits a segment at the
/// exact instant another enters does <b>not</b> conflict — the hand-off is contiguous,
/// not overlapping — which matches how agents chain segment-to-segment along a path.
/// </remarks>
public readonly record struct TimedSegment(
    PathSegment Segment,
    DateTimeOffset EnterAt,
    DateTimeOffset ExitAt)
{
    /// <summary>
    /// The duration the agent occupies the segment. Non-positive when the interval is
    /// empty or degenerate (<c>ExitAt &lt;= EnterAt</c>), in which case the occupancy is
    /// treated as occupying no time and can never overlap another interval.
    /// </summary>
    public TimeSpan Duration => ExitAt - EnterAt;

    /// <summary>
    /// True when <paramref name="other"/> is on the <b>same segment</b> AND their
    /// half-open intervals overlap. Interval overlap is
    /// <c>EnterAt &lt; other.ExitAt &amp;&amp; other.EnterAt &lt; ExitAt</c>, so touching
    /// endpoints (one interval ending exactly where the other begins) do not overlap.
    /// A degenerate interval (<c>ExitAt &lt;= EnterAt</c>) overlaps nothing.
    /// </summary>
    public bool OverlapsInterval(TimedSegment other)
    {
        if (!Segment.Equals(other.Segment))
        {
            return false;
        }

        return EnterAt < other.ExitAt && other.EnterAt < ExitAt;
    }
}
