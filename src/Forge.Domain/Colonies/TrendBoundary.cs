namespace Forge.Domain.Colonies;

using Forge.Domain.Common;

/// <summary>
/// A point in <em>simulated</em> time at which a colony's active consumption pattern changes,
/// expressed as pure data (Req 12.3). A trend boundary says: "from <see cref="StartsAt"/> onward
/// (until a later boundary supersedes it), scale each gel type's base consumption rate by
/// <see cref="Multiplier"/>."
/// <para>
/// This type deliberately contains <b>no generation logic</b>. Authoritative demand generation
/// lives in <c>Forge.Simulation.ColonyDemandSimulator</c> (task 27), which consumes these
/// boundaries. The only behavior exposed here is a pure, deterministic <em>selection</em> helper
/// (<see cref="DemandProfile.MultiplierAt"/>) used to answer "which pattern is active at time
/// <c>t</c>?" — it never produces orders.
/// </para>
/// </summary>
/// <param name="StartsAt">
/// The simulated time from which this boundary's pattern becomes active. Boundaries are ordered by
/// this value; the active boundary at a query time <c>t</c> is the latest boundary whose
/// <see cref="StartsAt"/> is at or before <c>t</c>.
/// </param>
/// <param name="Multiplier">
/// The non-negative, finite multiplier applied to base consumption rates while this boundary is
/// active (Req 12.6). A value of <c>1.0</c> leaves base rates unchanged; values above/below 1
/// model surges/lulls in colony demand.
/// </param>
public sealed record TrendBoundary(DateTimeOffset StartsAt, double Multiplier)
{
    /// <summary>
    /// Validate a single boundary's attributes (Req 12.6). <see cref="Multiplier"/> must be finite
    /// and non-negative. Returns the boundary on success or a <see cref="DomainError.Validation"/>
    /// naming the offending attribute on failure.
    /// </summary>
    public static Result<TrendBoundary> Create(DateTimeOffset startsAt, double multiplier)
    {
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier))
        {
            return DomainError.Validation(
                $"Trend boundary multiplier must be a finite number but was {multiplier}.",
                nameof(Multiplier));
        }

        if (multiplier < 0)
        {
            return DomainError.Validation(
                $"Trend boundary multiplier must be non-negative but was {multiplier}.",
                nameof(Multiplier));
        }

        return new TrendBoundary(startsAt, multiplier);
    }
}
