using Forge.Api.Forecasting;
using Forge.Application.Forecasting;
using Forge.Contracts.Dtos;
using Forge.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

/// <summary>
/// The REST endpoint for the human-in-the-loop forecast workflow (Req 22.1, 22.2, 22.3; design "ML
/// Forecasting and Human-in-the-Loop"). <see cref="Get"/> exposes the produced forecasts currently
/// available for operator review so the operator (or client) can pull them — Req 22.1 requires a
/// produced forecast to be reviewable within 5 seconds of production; the producer adds forecasts to
/// the review store as they are produced and this endpoint surfaces them (the timing itself is a
/// non-functional target owned by the producer/host, not a scheduler built here).
/// <see cref="SubmitDecisionAsync"/> applies an accept/override decision through
/// <see cref="SubmitForecastDecisionHandler"/>; an invalid override is rejected as a mapped <c>400</c>
/// (Req 22.4).
/// </summary>
[ApiController]
[Route("api/forecasts")]
public sealed class ForecastController : ControllerBase
{
    private readonly IForecastReviewStore _store;
    private readonly SubmitForecastDecisionHandler _handler;

    /// <summary>
    /// Construct the controller over the review store it reads/decides against and the decision handler
    /// it applies operator decisions through.
    /// </summary>
    public ForecastController(IForecastReviewStore store, SubmitForecastDecisionHandler handler)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Expose the produced forecasts currently available for operator review, each with its id
    /// (Req 22.1). Read-only pull; never mutates state.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ForecastDecisionResponse>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ForecastDecisionResponse>> Get()
    {
        var forecasts = _store.GetAll()
            .Select(kvp => new ForecastDecisionResponse(
                kvp.Key,
                kvp.Value.State.ToWireName(),
                kvp.Value.ToDto()))
            .ToArray();

        return Ok(forecasts);
    }

    /// <summary>
    /// Submit an operator accept/override decision for a produced forecast (Req 22.2, 22.3). Returns
    /// <c>200 OK</c> with the decided forecast on success; <c>404</c> when the forecast id is unknown;
    /// and a mapped <c>400</c> for an invalid override or a decision on an already-settled forecast
    /// (Req 22.4).
    /// </summary>
    /// <param name="request">The decision: forecast id, kind, operator id, and optional override value.</param>
    /// <param name="ct">Request cancellation token.</param>
    [HttpPost("decisions")]
    [ProducesResponseType(typeof(ForecastDecisionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ForecastDecisionResponse>> SubmitDecisionAsync(
        [FromBody] SubmitForecastDecisionRequest request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new ApiErrorDto(
                ErrorKind.Validation.ToString(), "Request body is required.", null));
        }

        if (!TryParseKind(request.Kind, out var kind))
        {
            return DomainError
                .Validation($"Unknown forecast decision kind '{request.Kind}'.", nameof(request.Kind))
                .ToProblem();
        }

        if (!_store.TryGet(request.ForecastId, out var lifecycle))
        {
            return NotFound();
        }

        var command = new SubmitForecastDecisionCommand(kind, request.OperatorId, request.OverrideValue);
        var result = await _handler.HandleAsync(lifecycle, command, ct).ConfigureAwait(false);

        if (!result.TryGet(out var outcome, out var error))
        {
            return error.ToProblem();
        }

        // Persist the settled forecast so a subsequent review/decision sees its new state (Req 22.4:
        // a second decision on a settled forecast is then rejected by the handler).
        _store.Update(request.ForecastId, outcome.Lifecycle);

        var response = new ForecastDecisionResponse(
            request.ForecastId,
            outcome.State.ToWireName(),
            outcome.Lifecycle.ToDto());

        return Ok(response);
    }

    /// <summary>Parse the wire decision kind case-insensitively; false for an unknown kind.</summary>
    private static bool TryParseKind(string? raw, out ForecastDecisionKind kind) =>
        Enum.TryParse(raw, ignoreCase: true, out kind) && Enum.IsDefined(kind);
}
