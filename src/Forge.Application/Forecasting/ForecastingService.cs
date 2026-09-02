using Forge.Application.Abstractions;
using Forge.Domain.Common;

namespace Forge.Application.Forecasting;

/// <summary>
/// The Application-layer orchestration over the <see cref="IMLPredictor"/> ML seam (task 25.1;
/// design "ML Forecasting and Human-in-the-Loop"; Req 9.4, 21.3, 21.4, 21.5, 21.6).
/// <para>
/// It requests demand forecasts and inventory-risk assessments through the predictor abstraction
/// and, when the predictor cannot answer, substitutes a deterministic <em>fallback</em> result
/// flagged <c>IsFallback</c> so the caller can tell an ML answer from a degraded one (Req 21.5). It
/// also produces the initial <see cref="ForecastState.Pending"/> <see cref="ForecastLifecycle"/> the
/// human-in-the-loop review (task 24.6) builds on.
/// </para>
/// <para>
/// <b>ML boundary (Req 21.6, 6).</b> This is pure orchestration over the abstraction — no ML.NET /
/// ONNX and no ML libraries live here (nor anywhere in the Domain). The concrete Phase-1 baseline
/// (<c>StatisticalBaselinePredictor</c>) is Infrastructure task 31.2; Phase-2 models swap in behind
/// the same interface (Req 21.2).
/// </para>
/// <para>
/// <b>Fallback value policy (Req 21.5).</b> The fallback is a deterministic <em>zero baseline</em>:
/// when the predictor is unavailable, throws, or returns nothing, demand is forecast as <c>0</c>
/// units and risk as a score of <c>0</c>, each flagged <c>IsFallback = true</c>. Zero is chosen as
/// the conservative "no signal" default — it introduces no fabricated demand or risk into
/// downstream planning while making the degraded state explicit via the flag. It depends only on the
/// request inputs (colony / gel type / horizon), so identical inputs always yield an identical
/// fallback, satisfying the reproducibility the core requires. A non-zero naive baseline
/// (e.g. moving-average) is deliberately deferred to the concrete predictor (task 31.2) so that a
/// <em>fallback</em> here never masquerades as a real estimate.
/// </para>
/// <para>
/// <b>Availability handling (Req 21.4, 21.5).</b> The service treats three conditions as
/// "unavailable" and falls back for all of them: <see cref="IMLPredictor.IsAvailable"/> is
/// <c>false</c>, a predictor call throws, or a call returns <c>null</c>. This keeps a flaky or
/// half-broken predictor from propagating exceptions into the tick pipeline. Cancellation
/// (<see cref="OperationCanceledException"/>) is <em>not</em> swallowed — it propagates so a
/// cancelled tick unwinds normally rather than silently degrading to a fallback.
/// </para>
/// </summary>
public sealed class ForecastingService
{
    private readonly IMLPredictor _predictor;

    /// <summary>Create the orchestrator over the given ML predictor seam.</summary>
    /// <param name="predictor">The predictor abstraction (concrete impl is Infrastructure task 31.2).</param>
    public ForecastingService(IMLPredictor predictor)
    {
        ArgumentNullException.ThrowIfNull(predictor);
        _predictor = predictor;
    }

    /// <summary>
    /// Request a demand forecast for <paramref name="colony"/> + <paramref name="gelType"/> over
    /// <paramref name="horizon"/> through the predictor, returning a fresh <see cref="ForecastState.Pending"/>
    /// <see cref="ForecastLifecycle"/> ready for operator review (Req 9.4, 21.3).
    /// <para>
    /// When the predictor is unavailable / throws / returns nothing, the returned lifecycle wraps the
    /// deterministic zero-baseline fallback flagged <c>IsFallback = true</c> (Req 21.5). The result is
    /// always <see cref="ForecastState.Pending"/>; accept / override / deadline transitions are task 24.6.
    /// </para>
    /// </summary>
    /// <param name="colony">The colony to forecast for.</param>
    /// <param name="gelType">The gel type to forecast for.</param>
    /// <param name="horizon">The forecast horizon.</param>
    /// <param name="ct">Cancellation token; cancellation propagates (is not treated as a fallback).</param>
    /// <returns>A <see cref="ForecastLifecycle"/> in the <see cref="ForecastState.Pending"/> state.</returns>
    public async Task<ForecastLifecycle> RequestForecastAsync(
        ColonyId colony,
        GelTypeId gelType,
        TimeSpan horizon,
        CancellationToken ct = default)
    {
        var forecast = await ForecastDemandAsync(colony, gelType, horizon, ct).ConfigureAwait(false);
        return ForecastLifecycle.Pending(forecast);
    }

    /// <summary>
    /// Forecast expected demand through the predictor, falling back to the deterministic zero
    /// baseline when the predictor is unavailable / throws / returns nothing (Req 21.3, 21.4, 21.5).
    /// </summary>
    /// <returns>
    /// The predictor's forecast when available; otherwise <see cref="FallbackForecast"/> flagged
    /// <c>IsFallback = true</c>.
    /// </returns>
    public async Task<DemandForecast> ForecastDemandAsync(
        ColonyId colony,
        GelTypeId gelType,
        TimeSpan horizon,
        CancellationToken ct = default)
    {
        if (!_predictor.IsAvailable)
        {
            return FallbackForecast(colony, gelType, horizon);
        }

        try
        {
            var forecast = await _predictor
                .ForecastDemandAsync(colony, gelType, horizon, ct)
                .ConfigureAwait(false);

            // A null from the seam is treated as "no answer" → fallback (Req 21.5).
            return forecast ?? FallbackForecast(colony, gelType, horizon);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a real unwind, not a degraded predictor — do not swallow it.
            throw;
        }
        catch
        {
            // Any other predictor failure degrades to the deterministic fallback (Req 21.5).
            return FallbackForecast(colony, gelType, horizon);
        }
    }

    /// <summary>
    /// Assess inventory risk through the predictor, falling back to the deterministic zero-risk
    /// baseline when the predictor is unavailable / throws / returns nothing (Req 21.4, 21.5).
    /// </summary>
    /// <param name="snapshot">The inventory snapshot to assess.</param>
    /// <param name="ct">Cancellation token; cancellation propagates (is not treated as a fallback).</param>
    /// <returns>
    /// The predictor's risk score when available; otherwise <see cref="FallbackRisk"/> flagged
    /// <c>IsFallback = true</c>.
    /// </returns>
    public async Task<RiskScore> AssessInventoryRiskAsync(
        InventorySnapshot snapshot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!_predictor.IsAvailable)
        {
            return FallbackRisk();
        }

        try
        {
            var risk = await _predictor
                .AssessInventoryRiskAsync(snapshot, ct)
                .ConfigureAwait(false);

            return risk ?? FallbackRisk();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return FallbackRisk();
        }
    }

    /// <summary>
    /// The deterministic fallback demand forecast (Req 21.5): a zero-unit baseline for the requested
    /// colony / gel type / horizon, flagged <c>IsFallback = true</c>. Deterministic in its inputs.
    /// </summary>
    public static DemandForecast FallbackForecast(ColonyId colony, GelTypeId gelType, TimeSpan horizon) =>
        new(colony, gelType, horizon, ExpectedDemand: 0d, IsFallback: true);

    /// <summary>
    /// The deterministic fallback inventory-risk score (Req 21.5): a zero (lowest) risk baseline
    /// flagged <c>IsFallback = true</c>.
    /// </summary>
    public static RiskScore FallbackRisk() => new(Score: 0d, IsFallback: true);
}
