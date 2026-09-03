using Forge.Contracts.Dtos;

namespace Forge.Api.Controllers;

/// <summary>
/// The request body the Forecast endpoint accepts to submit an operator's accept/override decision on
/// a produced forecast (Req 22.2, 22.3). The <see cref="ForecastId"/> identifies which reviewable
/// forecast the decision applies to; <see cref="OperatorId"/> is recorded on an override audit
/// (Req 22.5).
/// <para>
/// For an accept, leave <see cref="OverrideValue"/> null. For an override, supply the operator's input
/// <em>as a raw string</em> in <see cref="OverrideValue"/> so an empty / non-numeric submission is
/// rejectable (Req 22.4) — the decision handler parses and range-checks it against
/// <c>0..999,999,999</c>. <see cref="Kind"/> is the wire string <c>"Accept"</c> or <c>"Override"</c>.
/// </para>
/// </summary>
/// <param name="ForecastId">The reviewable forecast the decision applies to.</param>
/// <param name="Kind">The decision kind: <c>"Accept"</c> or <c>"Override"</c> (case-insensitive).</param>
/// <param name="OperatorId">The submitting operator's identity (recorded on an override audit).</param>
/// <param name="OverrideValue">The raw override value for an override; null/ignored for an accept.</param>
public sealed record SubmitForecastDecisionRequest(
    Guid ForecastId,
    string Kind,
    string OperatorId,
    string? OverrideValue = null);

/// <summary>
/// The success response for a submitted forecast decision: the id, the resulting lifecycle state, and
/// the decided forecast projected to its transport DTO (Req 22.2, 22.3).
/// </summary>
/// <param name="ForecastId">The forecast the decision was applied to.</param>
/// <param name="State">The resulting lifecycle state (<c>Accepted</c> / <c>Overridden</c> / ...).</param>
/// <param name="Forecast">The decided forecast projected to its transport DTO.</param>
public sealed record ForecastDecisionResponse(
    Guid ForecastId,
    string State,
    DemandForecastDto Forecast);
