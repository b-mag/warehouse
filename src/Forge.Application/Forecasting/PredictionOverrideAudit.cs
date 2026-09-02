namespace Forge.Application.Forecasting;

/// <summary>
/// The immutable audit record captured when an operator applies a validated
/// <c>Prediction_Override</c> to a forecast (Req 22.5; design "ML Forecasting and
/// Human-in-the-Loop").
/// <para>
/// When an override is accepted, <see cref="SubmitForecastDecisionHandler"/> records the original
/// forecast value, the operator-supplied override value, the operator's identity, and the time of the
/// override so the human intervention is fully attributable. The record is produced only on the
/// success path; a rejected override (invalid / non-numeric / empty) produces no audit and leaves the
/// forecast unchanged (Req 22.4).
/// </para>
/// </summary>
/// <param name="Colony">The colony the overridden forecast is for.</param>
/// <param name="GelType">The gel type the overridden forecast is for.</param>
/// <param name="OriginalValue">The forecast's original expected-demand value before the override (Req 22.5).</param>
/// <param name="OverrideValue">The validated operator-supplied replacement value in <c>0..999,999,999</c> (Req 22.3, 22.5).</param>
/// <param name="OperatorId">The identity of the operator who submitted the override (Req 22.5).</param>
/// <param name="Timestamp">The time the override was applied, taken from the clock (Req 22.5).</param>
public sealed record PredictionOverrideAudit(
    Forge.Domain.Common.ColonyId Colony,
    Forge.Domain.Common.GelTypeId GelType,
    double OriginalValue,
    long OverrideValue,
    string OperatorId,
    DateTimeOffset Timestamp);
