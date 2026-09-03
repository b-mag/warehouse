using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Api.Controllers;

/// <summary>
/// The REST endpoint for creating colony orders (Req 9.1, 9.2; design "REST controllers"). It maps a
/// <see cref="CreateColonyOrderRequest"/> to a <see cref="CreateColonyOrderCommand"/> and issues it
/// through <see cref="IWarehouseCommandGateway"/> — the same use-case handler the Simulation driver's
/// demand simulator drives, so an operator and a driver create orders the same way (design "The Input
/// Driver seam"). An expected rejection (unknown gel type, quantity &lt; 1, invalid delivery window)
/// comes back as a mapped HTTP error via <see cref="ApiResults"/> rather than a thrown exception.
/// </summary>
[ApiController]
[Route("api/orders")]
public sealed class OrdersController : ControllerBase
{
    private readonly IWarehouseCommandGateway _gateway;

    /// <summary>Construct the controller over the command gateway it issues the create command through.</summary>
    public OrdersController(IWarehouseCommandGateway gateway)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
    }

    /// <summary>
    /// Create a colony order (Req 9.1, 9.2). Returns <c>201 Created</c> with the new order id on
    /// success, or a mapped error (validation → <c>400</c>) with the rejection detail in the body.
    /// </summary>
    /// <param name="request">The order to create (colony, lines, delivery window).</param>
    /// <param name="ct">Request cancellation token.</param>
    [HttpPost]
    [ProducesResponseType(typeof(CreateColonyOrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiErrorDto), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateColonyOrderResponse>> CreateAsync(
        [FromBody] CreateColonyOrderRequest request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new ApiErrorDto(
                ErrorKind.Validation.ToString(), "Request body is required.", null));
        }

        var lines = (request.Lines ?? Array.Empty<CreateColonyOrderLineRequest>())
            .Select(l => new ColonyOrderLine(new GelTypeId(l.GelTypeId), l.Quantity))
            .ToArray();

        var command = new CreateColonyOrderCommand(
            new ColonyId(request.ColonyId),
            lines,
            request.DeliveryWindowStart,
            request.DeliveryWindowEnd);

        var result = await _gateway.CreateColonyOrderAsync(command, ct).ConfigureAwait(false);

        if (!result.TryGet(out var orderId, out var error))
        {
            return error.ToProblem();
        }

        var response = new CreateColonyOrderResponse(orderId.Value);
        return Created($"api/orders/{orderId.Value}", response);
    }
}
