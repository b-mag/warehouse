using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Docks;
using Forge.Application.Simulation;
using Forge.Application.Slotting;
using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Events;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;

namespace Forge.Application.Inbound;

/// <summary>
/// The WMS Core use-case handler for an inbound gel receipt (design "Use-case handlers";
/// Req 11.2, 11.3, 11.4, 14.1, 14.6). It is invoked with a
/// <see cref="RecordInboundGelReceiptCommand"/> by whichever driver is wired (the Simulation
/// driver's arrival generator in Phase 1) through
/// <see cref="IWarehouseCommandGateway.RecordInboundGelReceiptAsync"/>, and it turns that arrival
/// into stored inventory by:
/// <list type="number">
///   <item><description>
///     <b>Creating the inbound lot with a derived expiry (Req 11.4).</b> The gel type is resolved
///     from the command's <see cref="RecordInboundGelReceiptCommand.GelTypeId"/> via
///     <see cref="IGelTypeCatalog"/>; the lot is created through
///     <see cref="GelLot.Create(GelLotId, GelType, DateTimeOffset, int, int, ZoneId?)"/>, which
///     derives <c>ExpiresAt = ProducedAt + Formulation.NominalShelfLife</c> so the handler never
///     computes expiry itself. An unknown gel type or a non-positive quantity is rejected up front,
///     leaving inventory untouched.
///   </description></item>
///   <item><description>
///     <b>Receiving at a dock bay (Req 14.1).</b> The receipt first coordinates with the
///     <see cref="DockScheduler"/> for an inbound slot on the command's dock bay. <b>How a "received
///     at dock" is represented:</b> the accelerated tick pipeline (task 24.4) drives dock <em>timing</em>,
///     so this handler models the receipt as a minimal inbound <see cref="DockOperation"/> occupying a
///     short slot anchored at the current clock time (<see cref="IClock.Now"/>). Requesting that slot
///     is exactly the "receive at a dock bay" step: the scheduler either assigns the bay (receipt
///     proceeds) or, when the bay is occupied/closed/expired, queues the operation.
///   </description></item>
///   <item><description>
///     <b>No dock slot → blocked receiving (Req 14.6).</b> When the scheduler could not assign the
///     slot the arrival is queued (the scheduler already holds it in the bay's FIFO queue), the
///     receiving backlog is incremented on <see cref="WarehouseMetrics"/>, and both a
///     <see cref="DockBlocked"/> and a <see cref="BlockedArrival"/> domain event are raised. No
///     put-away task is created and no lot is stored, leaving inventory consistent.
///   </description></item>
///   <item><description>
///     <b>Put-away via slotting (Req 11.2, 14.1).</b> With a dock slot secured, the active
///     <see cref="ISlottingStrategy"/> (default velocity-affinity) selects a compatible zone with
///     capacity over the current zones, consulting a <see cref="ZoneSnapshotOccupancy"/>. On success
///     a <see cref="WarehouseTaskType.PutAway"/> <see cref="WarehouseTask"/> is generated to store the
///     lot in the chosen zone, the lot is staged via <see cref="IGelLotRepository"/> and the task via
///     <see cref="ITaskRepository"/>, and the unit of work is committed atomically.
///   </description></item>
///   <item><description>
///     <b>Unslottable → blocked placement (Req 11.3, 16.3).</b> When no compatible zone with
///     available capacity exists the strategy returns an unslottable result; the handler raises a
///     <see cref="BlockedPlacement"/> event and does <b>not</b> create an infeasible put-away task,
///     leaving inventory consistent (nothing is committed).
///   </description></item>
/// </list>
/// <para>
/// The handler depends only on Application abstractions + Domain types (no Infrastructure, no
/// Simulation project), and is deterministic given identical repository/catalog/clock state and
/// inputs: the slotting strategy is deterministic (Req 16.5), the dock scheduler is deterministic
/// (Req 17.5), and the handler performs no RNG. All events are published through
/// <see cref="IEventBus"/> after any state change is committed.
/// </para>
/// </summary>
public sealed class RecordInboundGelReceiptHandler
{
    /// <summary>
    /// The modeled duration of an inbound receipt's dock slot. The tick pipeline drives real dock
    /// timing; this is only the minimal interval the handler uses to represent "received at dock"
    /// when coordinating with the <see cref="DockScheduler"/> at the current clock time.
    /// </summary>
    private static readonly TimeSpan ReceiptSlotDuration = TimeSpan.FromMinutes(1);

    private readonly IGelTypeCatalog _gelTypes;
    private readonly IGelLotRepository _lots;
    private readonly IZoneRepository _zones;
    private readonly ITaskRepository _tasks;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISlottingStrategy _slotting;
    private readonly DockScheduler _dockScheduler;
    private readonly WarehouseMetrics _metrics;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;

    /// <summary>
    /// Construct the handler. <paramref name="slotting"/> is the active strategy (default
    /// velocity-affinity, selected by the operator's slotting-strategy parameter — Req 20.7).
    /// <paramref name="metrics"/> owns the receiving backlog counter incremented on blocked receiving
    /// (Req 14.6); it is injected (rather than the handler owning its own counter) so the whole core
    /// shares one backlog surface with the tick pipeline's metrics stage.
    /// </summary>
    public RecordInboundGelReceiptHandler(
        IGelTypeCatalog gelTypes,
        IGelLotRepository lots,
        IZoneRepository zones,
        ITaskRepository tasks,
        IUnitOfWork unitOfWork,
        ISlottingStrategy slotting,
        DockScheduler dockScheduler,
        WarehouseMetrics metrics,
        IEventBus eventBus,
        IClock clock)
    {
        _gelTypes = gelTypes ?? throw new ArgumentNullException(nameof(gelTypes));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _slotting = slotting ?? throw new ArgumentNullException(nameof(slotting));
        _dockScheduler = dockScheduler ?? throw new ArgumentNullException(nameof(dockScheduler));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Handle a <see cref="RecordInboundGelReceiptCommand"/> (Req 11.2, 11.3, 11.4, 14.1, 14.6).
    /// Returns a successful <see cref="Result"/> when the receipt produced a committed put-away, and a
    /// typed rejection otherwise (invalid receipt, blocked receiving, or blocked placement). Every
    /// rejection leaves inventory unchanged.
    /// </summary>
    public async Task<Result> HandleAsync(RecordInboundGelReceiptCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // ---- 1. Validate the receipt and create the inbound lot with a derived expiry (Req 11.4). ----
        if (command.Quantity <= 0)
        {
            return DomainError.InvalidRequest(
                $"An inbound gel receipt quantity must be greater than zero; got {command.Quantity}.");
        }

        var gelType = await _gelTypes.GetByIdAsync(command.GelTypeId, ct).ConfigureAwait(false);
        if (gelType is null)
        {
            return DomainError.InvalidRequest(
                $"Unknown gel type {command.GelTypeId} for the inbound gel receipt.");
        }

        var now = _clock.Now;

        // Expiry is derived inside GelLot.Create from the formulation's nominal shelf-life (Req 11.4);
        // the handler never computes it. The lot starts zone-less; put-away assigns storage.
        var lot = GelLot.Create(GelLotId.New(), gelType, command.ProducedAt, command.Quantity);

        // ---- 2. Receive at a dock bay (Req 14.1) by coordinating with the dock scheduler. ----
        // Model "received at dock" as a minimal inbound slot anchored at 'now'; the tick pipeline
        // drives real dock timing (see type-level docs).
        var slotResult = DockSlot.Create(now, now + ReceiptSlotDuration, DockOperationKind.Inbound);
        if (slotResult.IsFailure)
        {
            // Defensive: a positive ReceiptSlotDuration always yields a valid slot, so this is a
            // programming fault rather than an expected rejection.
            return slotResult.Error;
        }

        var receipt = new DockOperation(Guid.NewGuid(), command.DockBayId, slotResult.Value);
        var dockOutcome = _dockScheduler.Request(receipt, now);

        // Could not receive at the dock: the slot ended, the bay is unknown/closed (SlotUnavailable),
        // or the bay is occupied for the slot (queued). Either way no Dock_Bay slot is available.
        if (dockOutcome.IsFailure || !dockOutcome.Value.IsAssigned)
        {
            return await BlockReceivingAsync(command, lot, now, ct).ConfigureAwait(false);
        }

        // ---- 3. Slot the lot into a compatible zone via the active strategy (Req 11.2, 14.1). ----
        var zones = await _zones.ListAllAsync(ct).ConfigureAwait(false);
        var occupancy = new ZoneSnapshotOccupancy(zones);
        var slotting = _slotting.SelectZone(lot, gelType, zones, occupancy);

        // ---- 4. No compatible zone with capacity: blocked placement (Req 11.3, 16.3). ----
        if (slotting.IsUnslottable)
        {
            // Release the dock slot we just secured so a blocked placement does not tie up the bay,
            // and free any earliest-queued operation waiting on it (Req 17.5).
            _dockScheduler.Release(receipt, now);

            await _eventBus
                .PublishAsync(new BlockedPlacement(lot.Id, slotting.Error.Message, now), ct)
                .ConfigureAwait(false);

            // Do NOT create an infeasible put-away task and do NOT store the lot: inventory stays
            // consistent (nothing was staged/committed).
            return slotting.Error;
        }

        // ---- 5. Generate the PutAway task, stage lot + task, and commit atomically (Req 11.2, 14.1). ----
        var putAway = WarehouseTask.Create(
            WarehouseTaskId.New(),
            WarehouseTaskType.PutAway,
            origin: DockCell,
            destination: DockCell,
            estimatedDuration: TimeSpan.Zero);

        if (putAway.IsFailure)
        {
            // Zero estimated duration is always valid; defensive only.
            _dockScheduler.Release(receipt, now);
            return putAway.Error;
        }

        _lots.Add(lot);
        _tasks.Add(putAway.Value);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>
    /// The blocked-receiving path (Req 14.6): the arrival is already queued on the dock scheduler, so
    /// increment the receiving backlog and raise the dock-blocked + blocked-arrival events. Nothing is
    /// staged or committed, so inventory is left consistent.
    /// </summary>
    private async Task<Result> BlockReceivingAsync(
        RecordInboundGelReceiptCommand command,
        GelLot lot,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Add the queued arrival to the receiving backlog (Req 14.6). The BacklogChanged event, if any,
        // is published so the metrics surface reflects the new size (Req 14.7).
        var backlogChanged = _metrics.IncrementReceiving(1, now);

        await _eventBus.PublishAsync(new DockBlocked(command.DockBayId, now), ct).ConfigureAwait(false);
        await _eventBus
            .PublishAsync(
                new BlockedArrival(
                    lot.Id,
                    $"No dock bay slot available at {command.DockBayId} to receive the inbound gel lot.",
                    now),
                ct)
            .ConfigureAwait(false);

        if (backlogChanged is not null)
        {
            await _eventBus.PublishAsync(backlogChanged, ct).ConfigureAwait(false);
        }

        return DomainError.SlotUnavailable(
            $"Inbound gel lot could not be received: no dock bay slot available at {command.DockBayId}.");
    }

    /// <summary>
    /// The grid cell a put-away originates/terminates at for this Phase-1 modeling. A full path plan
    /// (dock cell → chosen zone cell) that derives the task's travel time is applied later by the
    /// assignment flow (task 19.1); the put-away task is generated here with a zero estimated duration
    /// and origin/destination at the dock, to be refined once zone-to-cell mapping is wired.
    /// </summary>
    private static readonly Cell DockCell = new(0, 0);
}
