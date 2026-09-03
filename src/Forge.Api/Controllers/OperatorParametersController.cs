using Forge.Application.OperatorParameters;
using Forge.Contracts.Dtos;
using Forge.Contracts.OperatorParameters;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

/// <summary>
/// The REST endpoint exposing the six operator-adjustable parameters and applying operator changes to
/// them (Req 20.1, 20.8; design "Operator Parameters"). <see cref="GetAsync"/> exposes the current
/// parameter state — simulation speed, workers on shift, open dock bays, inbound arrival rate, colony
/// demand multiplier, and the active slotting strategy (Req 20.1). <see cref="UpdateAsync"/> applies a
/// single change through <see cref="UpdateOperatorParameterHandler"/>, which validates + applies it and
/// (on success) publishes the updated state so all clients converge (Req 20.9); an out-of-range or
/// wrong-type value is rejected as a mapped <c>400</c> naming the invalid parameter (Req 20.8).
/// </summary>
[ApiController]
[Route("api/operator-parameters")]
public sealed class OperatorParametersController : ControllerBase
{
    private readonly OperatorParameterState _state;
    private readonly UpdateOperatorParameterHandler _handler;

    /// <summary>
    /// Construct the controller over the live parameter state it reads for <see cref="GetAsync"/> and
    /// the update handler it applies changes through for <see cref="UpdateAsync"/>.
    /// </summary>
    public OperatorParametersController(
        OperatorParameterState state,
        UpdateOperatorParameterHandler handler)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// Return the current values of the six operator parameters (Req 20.1). Read-only; never mutates
    /// state.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OperatorParameterStateDto), StatusCodes.Status200OK)]
    public ActionResult<OperatorParameterStateDto> Get() => Ok(_state.ToDto());

    /// <summary>
    /// Apply a single operator-parameter change (Req 20.8, 20.9). Returns <c>200 OK</c> with the
    /// updated parameter state on success, or a mapped <c>400</c> naming the invalid parameter when the
    /// value is out of range or of the wrong type (the previous value is retained).
    /// </summary>
    /// <param name="change">The single parameter change (key + string value).</param>
    /// <param name="ct">Request cancellation token.</param>
    [HttpPut]
    [ProducesResponseType(typeof(OperatorParameterStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OperatorParameterStateDto>> UpdateAsync(
        [FromBody] OperatorParameterDto change,
        CancellationToken ct)
    {
        if (change is null)
        {
            return BadRequest(new ApiErrorDto(
                Forge.Domain.Common.ErrorKind.Validation.ToString(),
                "Request body is required.",
                null));
        }

        var result = await _handler.Handle(change, ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return Ok(_state.ToDto());
    }
}
