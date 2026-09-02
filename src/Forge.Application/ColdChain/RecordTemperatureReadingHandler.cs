using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Application.Abstractions.Repositories;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Application.ColdChain;

/// <summary>
/// Use-case handler for recording a temperature reading against a gel lot's assigned cold-chain
/// zone (task 24.3; Req 6.2, 6.3, 6.4). Issued by the wired input driver's temperature generator
/// through <see cref="IWarehouseCommandGateway.RecordTemperatureReadingAsync"/>.
/// <para>
/// The handler is a thin orchestrator over the pure domain rule
/// <see cref="Forge.Domain.Gels.GelLot.RecordTemperature(TemperatureReading, TemperatureRange, out Forge.Domain.Events.TemperatureExcursion?)"/>:
/// <list type="number">
///   <item><description>Load the lot by id; an unknown lot is rejected with
///     <see cref="DomainError.InvalidRequest(string)"/> (no state change).</description></item>
///   <item><description>A lot with no assigned zone is rejected with
///     <see cref="DomainError.NoAssignedZone(string)"/> (Req 6.4). The domain rule enforces this too;
///     the handler short-circuits so it never issues a pointless zone lookup.</description></item>
///   <item><description>Load the lot's assigned <see cref="TemperatureZone"/> (needed for its
///     <see cref="TemperatureZone.AllowableRange"/>). A dangling zone reference is treated as an
///     invalid request.</description></item>
///   <item><description>Invoke the domain rule, which appends the reading to the lot's history in
///     timestamp order (Req 6.2) and, on an excursion, flags the lot <see cref="Forge.Domain.Gels.GelLot.AtRisk"/>
///     and surfaces a <see cref="Forge.Domain.Events.TemperatureExcursion"/> (Req 6.3).</description></item>
///   <item><description>Persist the lot mutation via <see cref="IUnitOfWork"/>, then publish the
///     excursion event (if any) via <see cref="IEventBus"/> (Req 6.3, 27.4).</description></item>
/// </list>
/// </para>
/// <para>
/// The command carries <c>Celsius</c> as a <see cref="double"/> (the driver seam speaks in
/// primitives); the domain works in <see cref="decimal"/> for exact inclusive-bound comparison, so
/// the handler converts once at the boundary.
/// </para>
/// </summary>
public sealed class RecordTemperatureReadingHandler
{
    private readonly IGelLotRepository _lots;
    private readonly IZoneRepository _zones;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEventBus _eventBus;

    /// <summary>Create the handler with its repository, unit-of-work, and event-bus collaborators.</summary>
    public RecordTemperatureReadingHandler(
        IGelLotRepository lots,
        IZoneRepository zones,
        IUnitOfWork unitOfWork,
        IEventBus eventBus)
    {
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    /// <summary>
    /// Handle a <see cref="RecordTemperatureReadingCommand"/> (Req 6.2, 6.3, 6.4). Returns
    /// <see cref="Result.Success()"/> when the reading is recorded (whether or not it was an
    /// excursion), or a typed rejection leaving all state unchanged.
    /// </summary>
    public async Task<Result> HandleAsync(RecordTemperatureReadingCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var lot = await _lots.GetByIdAsync(command.GelLotId, ct).ConfigureAwait(false);
        if (lot is null)
        {
            return DomainError.InvalidRequest($"No gel lot exists with id {command.GelLotId}.");
        }

        // Req 6.4: reject a zone-less lot before doing any further work.
        if (lot.AssignedZoneId is not { } zoneId)
        {
            return DomainError.NoAssignedZone(
                $"Gel lot {command.GelLotId} has no assigned zone; a temperature reading cannot be recorded against it.");
        }

        var zone = await _zones.GetByIdAsync(zoneId, ct).ConfigureAwait(false);
        if (zone is null)
        {
            return DomainError.InvalidRequest(
                $"Gel lot {command.GelLotId} references zone {zoneId}, which does not exist.");
        }

        // Cross the double->decimal boundary once, then apply the pure cold-chain rule (Req 6.2, 6.3).
        var reading = new TemperatureReading((decimal)command.Celsius, command.RecordedAt);
        var result = lot.RecordTemperature(reading, zone.AllowableRange, out var excursion);
        if (result.IsFailure)
        {
            return result;
        }

        _lots.Update(lot);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        // Req 6.3, 27.4: publish the excursion only after the mutation is durably persisted.
        if (excursion is not null)
        {
            await _eventBus.PublishAsync(excursion, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }
}
