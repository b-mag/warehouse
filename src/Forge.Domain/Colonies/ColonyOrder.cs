namespace Forge.Domain.Colonies;

using Forge.Domain.Common;

/// <summary>
/// An order a colony places on the warehouse (Req 12.1): a set of <see cref="OrderLine"/>s to be
/// fulfilled within a delivery window. Pure data — the order is produced by the create-order
/// use-case handler (task 24.1), which is invoked by the driver's demand simulator or the REST
/// Orders endpoint. This record holds no generation or fulfillment logic.
/// </summary>
/// <param name="Id">The order's strongly-typed identifier.</param>
/// <param name="Colony">The colony that placed the order.</param>
/// <param name="Lines">The requested gel types and quantities.</param>
/// <param name="DeliveryWindowStart">Start of the delivery window (simulated time).</param>
/// <param name="DeliveryWindowEnd">End of the delivery window (simulated time); must be after the start.</param>
public sealed record ColonyOrder(
    ColonyOrderId Id,
    ColonyId Colony,
    IReadOnlyList<OrderLine> Lines,
    DateTimeOffset DeliveryWindowStart,
    DateTimeOffset DeliveryWindowEnd);
