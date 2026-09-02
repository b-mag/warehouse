namespace Forge.Application.Forecasting;

/// <summary>
/// Which human-in-the-loop decision an operator is submitting for a produced forecast (Req 22.2, 22.3).
/// </summary>
public enum ForecastDecisionKind
{
    /// <summary>Accept the forecast as produced; its values apply downstream (Req 22.2).</summary>
    Accept = 0,

    /// <summary>Override the forecast with an operator-supplied value (Req 22.3, 22.5).</summary>
    Override,
}

/// <summary>
/// An operator's decision on a produced <see cref="ForecastLifecycle"/>, handled by
/// <see cref="SubmitForecastDecisionHandler"/> (Req 22.2–22.5; design "ML Forecasting and
/// Human-in-the-Loop").
/// <para>
/// For an <see cref="ForecastDecisionKind.Accept"/> the <see cref="OverrideValue"/> is ignored. For an
/// <see cref="ForecastDecisionKind.Override"/> the <see cref="OverrideValue"/> carries the operator's
/// input <em>as a raw string</em> deliberately: the validation in Req 22.4 must reject a non-numeric or
/// empty submission, which is only observable before parsing. The handler parses and range-checks it
/// against <c>0..999,999,999</c> and rejects anything that is empty, non-numeric, non-integer, or out of
/// range, retaining the original forecast (Req 22.4).
/// </para>
/// </summary>
/// <param name="Kind">Whether the operator is accepting or overriding (Req 22.2, 22.3).</param>
/// <param name="OperatorId">The submitting operator's identity, recorded on an override audit (Req 22.5).</param>
/// <param name="OverrideValue">
/// The raw operator-supplied override value for an <see cref="ForecastDecisionKind.Override"/>; null/ignored
/// for an accept. Kept as a string so empty / non-numeric input is rejectable (Req 22.4).
/// </param>
public sealed record SubmitForecastDecisionCommand(
    ForecastDecisionKind Kind,
    string OperatorId,
    string? OverrideValue = null)
{
    /// <summary>Create an accept decision for the given operator (Req 22.2).</summary>
    /// <param name="operatorId">The accepting operator's identity.</param>
    public static SubmitForecastDecisionCommand Accept(string operatorId) =>
        new(ForecastDecisionKind.Accept, operatorId);

    /// <summary>Create an override decision carrying the raw operator-supplied value (Req 22.3, 22.4, 22.5).</summary>
    /// <param name="operatorId">The overriding operator's identity.</param>
    /// <param name="overrideValue">The raw override value to validate against <c>0..999,999,999</c>.</param>
    public static SubmitForecastDecisionCommand Override(string operatorId, string? overrideValue) =>
        new(ForecastDecisionKind.Override, operatorId, overrideValue);
}
