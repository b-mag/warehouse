using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Commands;

/// <summary>
/// A single requested line on a <see cref="CreateColonyOrderCommand"/>: how many units of a given
/// gel type the colony is ordering (Req 9.1, 12.6). Mirrors the domain's order-line shape without
/// coupling the driver seam to the domain aggregate.
/// </summary>
/// <param name="GelTypeId">The gel type being ordered.</param>
/// <param name="Quantity">The requested quantity (validated by the handler; must be &gt;= 1 — Req 5.5).</param>
public sealed record ColonyOrderLine(GelTypeId GelTypeId, int Quantity);

/// <summary>
/// Command to create a colony order (Req 9.1, 9.2). Issued by the wired input driver's demand
/// simulator (authoritative demand) or by the REST Orders endpoint, both through
/// <see cref="IWarehouseCommandGateway.CreateColonyOrderAsync"/>. Carries the ordering colony,
/// the requested order lines, and the delivery window the order must be fulfilled within.
/// </summary>
/// <param name="ColonyId">The colony placing the order.</param>
/// <param name="Lines">The requested order lines (gel type + quantity).</param>
/// <param name="DeliveryWindowStart">Inclusive start of the delivery window.</param>
/// <param name="DeliveryWindowEnd">Exclusive/inclusive end of the delivery window (must be &gt; start).</param>
public sealed record CreateColonyOrderCommand(
    ColonyId ColonyId,
    IReadOnlyList<ColonyOrderLine> Lines,
    DateTimeOffset DeliveryWindowStart,
    DateTimeOffset DeliveryWindowEnd);

/// <summary>
/// Command to record an inbound gel receipt at a dock bay (Req 11.2, 11.3, 11.4). Issued by the
/// wired input driver's arrival generator through
/// <see cref="IWarehouseCommandGateway.RecordInboundGelReceiptAsync"/>. The handler derives the
/// lot's expiry from the formulation's nominal shelf-life (Req 11.4) and issues a put-away task via
/// <see cref="ISlottingStrategy"/>.
/// </summary>
/// <param name="GelTypeId">The gel type / formulation family of the received lot.</param>
/// <param name="ProducedAt">When the received lot was produced (drives expiry = ProducedAt + nominal shelf-life).</param>
/// <param name="Quantity">The received quantity.</param>
/// <param name="DockBayId">The dock bay the receipt arrives at.</param>
public sealed record RecordInboundGelReceiptCommand(
    GelTypeId GelTypeId,
    DateTimeOffset ProducedAt,
    int Quantity,
    DockBayId DockBayId);

/// <summary>
/// Command to record a temperature reading against a lot's assigned zone (Req 6.2, 6.3, 6.4).
/// Issued by the wired input driver's temperature generator through
/// <see cref="IWarehouseCommandGateway.RecordTemperatureReadingAsync"/>. The handler appends the
/// reading in timestamp order, detects excursions, and rejects a zone-less lot with
/// <see cref="ErrorKind.NoAssignedZone"/>.
/// </summary>
/// <param name="GelLotId">The lot the reading pertains to.</param>
/// <param name="Celsius">The measured temperature in degrees Celsius.</param>
/// <param name="RecordedAt">The reading's timestamp (used to keep history in timestamp order).</param>
public sealed record RecordTemperatureReadingCommand(
    GelLotId GelLotId,
    double Celsius,
    DateTimeOffset RecordedAt);
