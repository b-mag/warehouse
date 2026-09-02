namespace Forge.Application.Forecasting;

/// <summary>
/// The successful result of applying an operator decision (or the auto-accept deadline) to a
/// forecast (Req 22.2, 22.3, 22.5, 22.6; design "ML Forecasting and Human-in-the-Loop").
/// <para>
/// Carries the transitioned <see cref="Lifecycle"/> — in state <see cref="ForecastState.Accepted"/>,
/// <see cref="ForecastState.Overridden"/>, or <see cref="ForecastState.Accepted_By_Default"/> — whose
/// forecast values are the ones that apply downstream (the original for accept / auto-accept, the
/// operator's replacement for an override). <see cref="Audit"/> is present only for an override,
/// recording the original value, override value, operator id, and timestamp (Req 22.5); it is
/// <c>null</c> for accept and auto-accept, which capture no override audit.
/// </para>
/// <para>
/// A rejected decision (invalid / non-numeric / empty override) produces no outcome at all: the
/// handler returns a failure and the caller retains the original forecast unchanged (Req 22.4).
/// </para>
/// </summary>
/// <param name="Lifecycle">The transitioned forecast + its new lifecycle state.</param>
/// <param name="Audit">The override audit when the decision was an override; otherwise <c>null</c>.</param>
public sealed record ForecastDecisionOutcome(
    ForecastLifecycle Lifecycle,
    PredictionOverrideAudit? Audit)
{
    /// <summary>The forecast state after the decision, for convenience.</summary>
    public ForecastState State => Lifecycle.State;
}
