using Forge.Domain.Common;

namespace Forge.Application.Forecasting;

/// <summary>
/// The configurable auto-accept deadline for an operator's response to a produced
/// <see cref="DemandForecast"/> (Req 22.6; design "ML Forecasting and Human-in-the-Loop").
/// <para>
/// If the operator does not accept or override a forecast within this window of its production time,
/// <see cref="SubmitForecastDecisionHandler"/> applies the forecast as the default and moves it to
/// <see cref="ForecastState.Accepted_By_Default"/>. The deadline is bounded to the range
/// <c>1..168</c> hours (one hour to one week) with a default of <c>24</c> hours; a value outside the
/// range is a configuration error and is rejected via <see cref="Create(TimeSpan)"/> rather than
/// silently clamped.
/// </para>
/// </summary>
public readonly record struct ForecastDecisionDeadline
{
    /// <summary>The inclusive minimum deadline (Req 22.6).</summary>
    public static readonly TimeSpan Minimum = TimeSpan.FromHours(1);

    /// <summary>The inclusive maximum deadline of one week (Req 22.6).</summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromHours(168);

    /// <summary>The default deadline applied when none is configured (Req 22.6).</summary>
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromHours(24);

    private ForecastDecisionDeadline(TimeSpan duration) => Duration = duration;

    /// <summary>The configured deadline duration, guaranteed within <c>1..168</c> hours.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The default 24-hour deadline (Req 22.6).</summary>
    public static ForecastDecisionDeadline Default => new(DefaultDuration);

    /// <summary>
    /// Create a deadline from a duration, validating it lies within the inclusive
    /// <c>1..168</c>-hour range (Req 22.6). Returns a <see cref="DomainError.Validation(string, string?)"/>
    /// naming the parameter when out of range, leaving no state changed.
    /// </summary>
    /// <param name="duration">The requested deadline; must be within <c>1..168</c> hours.</param>
    public static Result<ForecastDecisionDeadline> Create(TimeSpan duration)
    {
        if (duration < Minimum || duration > Maximum)
        {
            return DomainError.Validation(
                $"Forecast decision deadline must be between {Minimum.TotalHours} and " +
                $"{Maximum.TotalHours} hours; got {duration.TotalHours}.",
                nameof(duration));
        }

        return new ForecastDecisionDeadline(duration);
    }

    /// <summary>
    /// Whether the deadline has elapsed for a forecast produced at <paramref name="producedAt"/> as
    /// evaluated at <paramref name="now"/>. The deadline is reached when at least <see cref="Duration"/>
    /// has passed since production, i.e. <c>now - producedAt &gt;= Duration</c> (Req 22.6).
    /// </summary>
    /// <param name="producedAt">The time the forecast was produced.</param>
    /// <param name="now">The current time to evaluate against (typically the clock's now).</param>
    public bool HasElapsed(DateTimeOffset producedAt, DateTimeOffset now) =>
        now - producedAt >= Duration;
}
