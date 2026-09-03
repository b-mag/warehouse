namespace Forge.Api.Controllers;

/// <summary>
/// A single requested line on a <see cref="CreateColonyOrderRequest"/>: a gel type and the requested
/// quantity (Req 9.1). This is the transport shape the Orders endpoint accepts; the controller maps it
/// to the driver-seam <see cref="Forge.Application.Abstractions.Commands.ColonyOrderLine"/>. Ids are
/// carried as <see cref="Guid"/> so the wire payload stays free of domain value types.
/// </summary>
/// <param name="GelTypeId">The gel type being ordered.</param>
/// <param name="Quantity">The requested quantity (validated by the handler; must be &gt;= 1).</param>
public sealed record CreateColonyOrderLineRequest(Guid GelTypeId, int Quantity);

/// <summary>
/// The request body the Orders endpoint accepts to create a colony order (Req 9.1). The controller
/// maps it to a <see cref="Forge.Application.Abstractions.Commands.CreateColonyOrderCommand"/> and
/// issues it through <see cref="Forge.Application.Abstractions.IWarehouseCommandGateway"/>; the handler
/// owns the actual validation (unknown gel type, quantity &lt; 1, delivery window ordering), so a bad
/// request comes back as a mapped <c>400</c> rather than being pre-validated here.
/// </summary>
/// <param name="ColonyId">The colony placing the order.</param>
/// <param name="Lines">The requested order lines (gel type + quantity).</param>
/// <param name="DeliveryWindowStart">Inclusive start of the delivery window.</param>
/// <param name="DeliveryWindowEnd">End of the delivery window (must be &gt; start).</param>
public sealed record CreateColonyOrderRequest(
    Guid ColonyId,
    IReadOnlyList<CreateColonyOrderLineRequest> Lines,
    DateTimeOffset DeliveryWindowStart,
    DateTimeOffset DeliveryWindowEnd);

/// <summary>
/// The success response for creating a colony order: the new order's id (Req 9.1, 9.2).
/// </summary>
/// <param name="OrderId">The identifier of the newly created colony order.</param>
public sealed record CreateColonyOrderResponse(Guid OrderId);
