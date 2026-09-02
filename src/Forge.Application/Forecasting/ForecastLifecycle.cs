using Forge.Contracts.Dtos;

namespace Forge.Application.Forecasting;

/// <summary>
/// A produced <see cref="DemandForecast"/> together with its human-in-the-loop lifecycle
/// <see cref="State"/> (design "ML Forecasting and Human-in-the-Loop"; Req 22).
/// <para>
/// This is the unit the forecasting orchestrator (task 25.1) hands off to the operator-review
/// workflow. A newly produced forecast is created <see cref="Pending"/> via
/// <see cref="Pending(DemandForecast)"/>; the accept / override / deadline transitions that move it
/// to <see cref="ForecastState.Accepted"/>, <see cref="ForecastState.Overridden"/>, or
/// <see cref="ForecastState.Accepted_By_Default"/> are applied by <c>SubmitForecastDecisionHandler</c>
/// (task 24.6) — deliberately <em>not</em> implemented here.
/// </para>
/// <para>
/// The record is immutable; a transition produces a new instance (records + <c>with</c>), so a
/// rejected decision can simply discard the candidate and retain the original, leaving state
/// unchanged (the pattern Req 22.4 relies on).
/// </para>
/// </summary>
/// <param name="Forecast">The underlying forecast (ML or fallback).</param>
/// <param name="State">The current lifecycle state.</param>
public sealed record ForecastLifecycle(DemandForecast Forecast, ForecastState State)
{
    /// <summary>
    /// Create a freshly produced forecast in the initial <see cref="ForecastState.Pending"/> state,
    /// awaiting an operator decision (design lifecycle <c>Pending → …</c>; Req 22).
    /// </summary>
    /// <param name="forecast">The produced forecast (ML or fallback).</param>
    public static ForecastLifecycle Pending(DemandForecast forecast)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        return new ForecastLifecycle(forecast, ForecastState.Pending);
    }

    /// <summary>
    /// Whether this forecast is a non-ML fallback produced because the predictor was unavailable
    /// (Req 21.5). Mirrors <see cref="DemandForecast.IsFallback"/>.
    /// </summary>
    public bool IsFallback => Forecast.IsFallback;

    /// <summary>
    /// Project this forecast + lifecycle state to the transport
    /// <see cref="DemandForecastDto"/> (Req 2.3, 23.4).
    /// <para>
    /// The DTO's <c>Quantity</c> is a whole-unit <see cref="long"/>; the forecast's continuous
    /// <see cref="DemandForecast.ExpectedDemand"/> is rounded to the nearest unit and clamped to be
    /// non-negative (demand cannot be negative). <c>State</c> is the canonical wire name of
    /// <see cref="State"/>, and <c>IsFallback</c> is carried through unchanged (Req 21.5).
    /// </para>
    /// </summary>
    public DemandForecastDto ToDto()
    {
        var rounded = Math.Round(Forecast.ExpectedDemand, MidpointRounding.AwayFromZero);
        var quantity = rounded <= 0d ? 0L : (long)rounded;

        return new DemandForecastDto(
            Colony: Forecast.Colony.Value,
            GelType: Forecast.GelType.Value,
            Horizon: Forecast.Horizon,
            Quantity: quantity,
            IsFallback: Forecast.IsFallback,
            State: State.ToWireName());
    }
}
