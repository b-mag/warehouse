using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.Common;

namespace Forge.Application.Abstractions;

/// <summary>
/// The command/event entrypoints the WMS Core exposes to any driver or REST controller (design
/// "The Input Driver seam"; Req 1.4, 2.6, 9.5). These are the same use-case handlers a REST
/// controller invokes, so a driver and an operator both drive the core the same way. The core
/// never generates these inputs itself — a wired <see cref="IWarehouseInputDriver"/> supplies them.
/// <para>
/// Each rejectable operation returns a <see cref="Result"/> / <see cref="Result{T}"/> so an expected
/// rejection leaves state unchanged and is inspected by the caller rather than thrown.
/// </para>
/// </summary>
public interface IWarehouseCommandGateway
{
    /// <summary>
    /// Create a colony order and generate its fulfillment tasks (Req 9.1, 9.2). Returns the new
    /// order's id on success or a typed rejection (e.g. invalid quantity/unknown gel type).
    /// </summary>
    Task<Result<ColonyOrderId>> CreateColonyOrderAsync(CreateColonyOrderCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Record an inbound gel receipt at a dock bay and issue a put-away task via slotting
    /// (Req 11.2, 11.3, 11.4). Rejections cover a blocked dock or no compatible zone.
    /// </summary>
    Task<Result> RecordInboundGelReceiptAsync(RecordInboundGelReceiptCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Record a temperature reading against a lot's assigned zone and detect excursions
    /// (Req 6.2, 6.3, 6.4). Rejects a zone-less lot with <see cref="ErrorKind.NoAssignedZone"/>.
    /// </summary>
    Task<Result> RecordTemperatureReadingAsync(RecordTemperatureReadingCommand cmd, CancellationToken ct = default);

    /// <summary>
    /// Apply the per-tick RULE stages for the given simulated delta (Req 10.4). This is core rule
    /// application invoked by a driver's tick loop; it generates no inputs and applies zero effect
    /// when <paramref name="simDelta"/> is zero (paused — Req 10.5).
    /// </summary>
    Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default);

    /// <summary>
    /// Read-only query returning the current inventory/order/task/starship state as a
    /// <see cref="SimulationSnapshotDto"/> without mutating simulation state (Req 9.3, 23.3).
    /// </summary>
    Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default);
}
