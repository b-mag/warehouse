using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Loading;
using Forge.Application.OperatorParameters;

using Forge.Domain.Colonies;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;

namespace Forge.Application.Simulation;

/// <summary>
/// The per-tick RULE APPLICATION handler (task 24.4) — the heart of the tick pipeline and the concrete
/// realization of the driver/core split (design.md "Simulation Tick Pipeline"). It runs the
/// <b>fixed-order</b> rule stages over the current simulated time and a supplied delta, over inputs
/// <em>already delivered as commands</em>. It is invoked by a driver's tick loop through
/// <see cref="IWarehouseCommandGateway.ApplyTickRulesAsync(TimeSpan, CancellationToken)"/>.
///
/// <para><b>Fixed stage order (design.md "Core rule application", Req 10.4):</b></para>
/// <list type="number">
///   <item><description><b>Expiry decay</b> — mark lots whose whole-second shelf-life reached zero and
///     publish one <see cref="LotExpired"/> per transition (Req 4).</description></item>
///   <item><description><b>Order-intake effects</b> — apply the engine-side effects of colony orders
///     received this tick (demand-driven fulfillment accounting), deterministically (Req 12.2, 12.3).</description></item>
///   <item><description><b>Agent movement</b> — advance each agent along its reserved path by
///     <c>speed × delta</c>, reserving/holding/re-planning deterministically, raising
///     <see cref="UnroutableTask"/> when no path exists (Req 18.4, 18.6, 19.2).</description></item>
///   <item><description><b>Starship loading</b> — within open windows run FEFO pick+load; raise
///     <see cref="LoadingWindowClosed"/> on window close (Req 13.2).</description></item>
///   <item><description><b>Metrics</b> — recompute receiving/outbound backlog sizes and throughput,
///     raising <see cref="BacklogChanged"/> on change via <see cref="WarehouseMetrics"/> (Req 14.x).</description></item>
/// </list>
///
/// <para><b>NO input generation (Req 1.8 — the architecture-test-enforced boundary).</b> This handler
/// generates no arrivals, no colony demand, no temperature readings, and never advances a clock. Those
/// are the driver's job (task 27). It only reads <see cref="IClock.Now"/> and applies rules for the
/// delta the driver hands it. It references no <c>Forge.Simulation</c> type and no generator.</para>
///
/// <para><b>Paused / zero-delta is a deterministic no-op (Req 10.4, 10.5).</b> When
/// <c>simDelta &lt;= 0</c> the handler applies zero effect: it neither mutates state nor publishes
/// events, and returns success immediately without touching the unit of work.</para>
///
/// <para><b>Reproducibility.</b> For an identical <c>(state, now, delta)</c> the next state is identical:
/// the stage order is fixed, every per-entity pass iterates in ascending-id order (lots, agents,
/// starships), and there is no RNG and no wall-clock read. All mutations are staged on the repositories
/// and committed in a single <see cref="IUnitOfWork"/> save; all collected domain events are published
/// through <see cref="IEventBus"/> only after the commit succeeds.</para>
/// </summary>
public sealed class ApplyTickRulesHandler
{
    private readonly IClock _clock;
    private readonly IGelLotRepository _lots;
    private readonly IZoneRepository _zones;
    private readonly IOrderRepository _orders;
    private readonly ITaskRepository _tasks;
    private readonly IPathPlanner _planner;
    private readonly StarshipLoadingService _loadingService;
    private readonly WarehouseMetrics _metrics;
    private readonly OperatorParameterState _operatorParameters;
    private readonly ITickStateProvider _tickState;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEventBus _eventBus;
    /// <summary>
    /// Construct the handler from the abstractions and collaborators it orchestrates. It depends only on
    /// Application abstractions + Domain/Application collaborators — never on concrete Infrastructure or
    /// on the Simulation driver (Req 1.4, 1.8, 9.5).
    /// </summary>
    /// <param name="clock">Supplies <see cref="IClock.Now"/>; the handler never advances it (Req 1.8, 10.6).</param>
    /// <param name="lots">Loads/stages gel lots for the expiry-decay stage + FEFO loading inventory.</param>
    /// <param name="zones">Temperature zones updated when PutAway completes into storage.</param>
    /// <param name="orders">Loads colony orders for the order-intake effects stage.</param>
    /// <param name="tasks">Loads/stages warehouse tasks (order-intake fulfillment + movement task lookup).</param>
    /// <param name="planner">The deterministic path planner used by the movement stage (Req 18.3).</param>
    /// <param name="loadingService">The window-admitted FEFO loading rule used by the loading stage (Req 13).</param>
    /// <param name="metrics">The metrics component for the recompute stage (Req 14.x).</param>
    /// <param name="tickState">Supplies the tick-scoped spatial/vessel state (see <see cref="TickState"/>).</param>
    /// <param name="unitOfWork">Commits all staged mutations atomically at the end of the tick.</param>
    /// <param name="eventBus">Publishes all collected domain events after the commit (Req 27.3, 27.4).</param>
    public ApplyTickRulesHandler(
        IClock clock,
        IGelLotRepository lots,
        IZoneRepository zones,
        IOrderRepository orders,
        ITaskRepository tasks,
        IPathPlanner planner,
        StarshipLoadingService loadingService,
        WarehouseMetrics metrics,
        OperatorParameterState operatorParameters,
        ITickStateProvider tickState,
        IUnitOfWork unitOfWork,
        IEventBus eventBus)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _loadingService = loadingService ?? throw new ArgumentNullException(nameof(loadingService));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _operatorParameters = operatorParameters ?? throw new ArgumentNullException(nameof(operatorParameters));
        _tickState = tickState ?? throw new ArgumentNullException(nameof(tickState));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
    }

    /// <summary>
    /// Apply the fixed-order per-tick rule stages for <paramref name="simDelta"/> at the current
    /// <see cref="IClock.Now"/> (Req 10.4). Matches
    /// <see cref="IWarehouseCommandGateway.ApplyTickRulesAsync(TimeSpan, CancellationToken)"/>.
    /// <para>
    /// Returns <see cref="Result.Success()"/> after committing all mutations and publishing all events.
    /// When <paramref name="simDelta"/> is non-positive the tick is a deterministic no-op: no state
    /// changes, no events, no commit (Req 10.4, 10.5).
    /// </para>
    /// </summary>
    /// <param name="simDelta">The simulated time to advance the rules by; non-positive ⇒ no-op.</param>
    /// <param name="ct">Cancellation token propagated to repositories, unit of work, and event bus.</param>
    public async Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default)
    {
        // Req 10.4 / 10.5: a paused (zero or negative delta) tick applies zero effect — no reads that
        // could mutate, no commit, no publish. Deterministic no-op.
        if (simDelta <= TimeSpan.Zero)
        {
            return Result.Success();
        }

        var now = _clock.Now;
        var events = new List<IDomainEvent>();

        // Track exactly the aggregates each stage mutated so only changed entities are staged for update.
        var mutatedLots = new List<GelLot>();
        var mutatedAgents = new List<Agent>();
        var mutatedStarships = new List<Starship>();
        var mutatedTasks = new List<WarehouseTask>();

        // ---- Stage 1: Expiry decay (Req 4). ----
        var allLots = await _lots.ListAllAsync(ct).ConfigureAwait(false);
        events.AddRange(TickStages.ExpiryDecay(allLots, now, mutatedLots));
        foreach (var lot in mutatedLots)
        {
            _lots.Update(lot);
        }

        // ---- Stage 2: Order-intake effects for orders received this tick (Req 12.2, 12.3). ----
        // Apply the engine-side effects of colony orders whose delivery window opened during this tick's
        // window (now-delta, now]. Task GENERATION happens at order-creation time in
        // CreateColonyOrderHandler (task 24.1); the effect this stage owns is the deterministic outbound
        // demand accounting that feeds the metrics stage. Iterating orders in id order keeps it
        // reproducible; it generates no orders (that is the driver's job — Req 1.8).
        var orders = await _orders.ListAllAsync(ct).ConfigureAwait(false);
        int outboundDemand = OrderIntakeDemand(orders, now, simDelta);

        // ---- Stage 3: Agent movement (Req 18.4, 18.6, 19.2). ----
        // ---- Stage 4: Starship loading (Req 13.2). ----
        // Both need the tick-scoped spatial/vessel state. When no spatial subsystem is wired the provider
        // returns null and these two stages are deterministic no-ops (headless cold-chain-only run).
        var state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
        if (state is not null)
        {
            // Phase-1 visual story: keep the movement agent list in sync with the operator's
            // WorkersOnShift slider so cones spawn/despawn (without stalling any in-flight tasks).
            _tickState.ApplyWorkerCount(_operatorParameters.WorkersOnShift);
            state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);

            int openDockBays = _operatorParameters.OpenDockBays;

            // Engine-owned ship lifecycle (fly in / load / fly out when full).
            TickStages.StarshipLifecycle(state!, now, simDelta, openDockBays);

            // Stage 3: advance agents. UnroutableTask events carry the task id an agent is executing; with
            // no persisted agent→task link yet, no id is resolvable, so the event is raised only when a
            // resolver is available. The pipeline still holds/re-plans deterministically.
            events.AddRange(TickStages.AgentMovement(
                state, _planner, now, simDelta, mutatedAgents, static _ => null));

            // Stage 4: load only ships that are in the Loading phase at an open berth.
            // Cap to 1 pallet/tick so fill+depart is visually readable (worker carry will own this later).
            Func<Starship, (GelTypeId GelType, int Quantity)?> demandFor = ship =>
            {
                if (!TickStages.CanLoadStarship(state!, ship))
                {
                    return null;
                }

                int remaining = ship.RemainingCapacity;
                if (remaining <= 0)
                {
                    return null;
                }

                // Resolve "open demand" from order lines whose delivery window includes 'now'.
                // (Phase-1 visual story: deterministic, order-driven loading without per-line fulfillment tracking.)
                var destinationOrders = orders
                    .Where(o =>
                        o.Colony == ship.Destination &&
                        o.DeliveryWindowStart <= now &&
                        o.DeliveryWindowEnd >= now)
                    .ToArray();

                if (destinationOrders.Length == 0)
                {
                    return null;
                }

                var line = destinationOrders
                    .OrderBy(o => o.Id)
                    .SelectMany(o => o.Lines)
                    .OrderBy(l => l.GelType.Value)
                    .FirstOrDefault();

                if (line is null)
                {
                    return null;
                }

                int requested = (int)Math.Ceiling(line.Quantity * _operatorParameters.DemandMultiplier);
                requested = Math.Clamp(
                    requested,
                    1,
                    Math.Min(remaining, VisualSimulationConstants.MaxPalletsLoadedPerTick));
                return (line.GelType, requested);
            };

            events.AddRange(TickStages.StarshipLoading(
                state,
                _loadingService,
                allLots,
                now,
                simDelta,
                mutatedStarships,
                demandFor));

            // Re-run lifecycle so a ship that just hit capacity this tick enters Departing immediately.
            TickStages.StarshipLifecycle(state!, now, TimeSpan.FromTicks(1), openDockBays);
        }

        // ---- Stage 3.5: Task execution — agents claim, travel to, and complete PutAway/Pick tasks. ----
        // This is what actually DRAINS the backlog: agents pick up unassigned put-away/pick work, are
        // routed to the task's destination, and the task completes when the agent arrives. Assignment
        // removes a task from the "unassigned" set (dropping the receiving backlog immediately), and
        // completion records processed lots into throughput. The active-agent cap is driven by the
        // operator's WORKERS-ON-SHIFT parameter so that control governs how fast the backlog drains.
        var unassignedTasks = await _tasks.GetUnassignedAsync(ct).ConfigureAwait(false);
        if (state is not null && unassignedTasks.Count + state.AgentTasks.Count > 0)
        {
            // Build an id->task lookup for the stage to resolve an agent's in-flight task (Phase A). We
            // load all tasks once (not per-lookup) so the stage stays synchronous and allocation-light.
            var allTasks = await _tasks.ListAllAsync(ct).ConfigureAwait(false);
            var tasksById = new Dictionary<WarehouseTaskId, WarehouseTask>(allTasks.Count);
            foreach (var task in allTasks)
            {
                tasksById[task.Id] = task;
            }

            int workersOnShift = _operatorParameters.WorkersOnShift;
            var execution = TickStages.TaskExecution(
                state,
                unassignedTasks,
                id => tasksById.TryGetValue(id, out var t) ? t : null,
                _planner,
                workersOnShift,
                now,
                simDelta,
                mutatedTasks,
                mutatedAgents);

            foreach (var task in mutatedTasks)
            {
                _tasks.Update(task);
            }

            events.AddRange(execution.Events);

            // PutAway complete → actually store into the zone so holding areas show inventory.
            if (execution.CompletedPutAwayLotIds.Count > 0)
            {
                var zonesById = (await _zones.ListAllAsync(ct).ConfigureAwait(false))
                    .ToDictionary(z => z.Id);
                var lotsById = allLots.ToDictionary(l => l.Id);

                foreach (var lotId in execution.CompletedPutAwayLotIds.OrderBy(id => id))
                {
                    if (!lotsById.TryGetValue(lotId, out var lot))
                    {
                        lot = await _lots.GetByIdAsync(lotId, ct).ConfigureAwait(false);
                        if (lot is null)
                        {
                            continue;
                        }
                    }

                    if (lot.AssignedZoneId is not { } zoneId ||
                        !zonesById.TryGetValue(zoneId, out var zone))
                    {
                        continue;
                    }

                    if (zone.TryStore(lot.Quantity).IsSuccess)
                    {
                        _zones.Update(zone);
                        mutatedLots.Add(lot); // keep save path active; lot already assigned
                    }
                }
            }

            // Completed put-away/pick tasks are processed lots — feed throughput (Req 14.5).
            if (execution.PutAwayCompleted > 0)
            {
                _metrics.RecordInboundProcessed(execution.PutAwayCompleted, simDelta.TotalSeconds);
            }

            if (execution.PickCompleted > 0)
            {
                _metrics.RecordOutboundProcessed(execution.PickCompleted, simDelta.TotalSeconds);
            }
        }

        // ---- Stage 5: Metrics recompute + BacklogChanged (Req 14.3, 14.4, 14.5, 14.7). ----
        // Receiving backlog = arrivals not yet absorbed by put-away/capacity; with the current seams the
        // per-tick receiving pressure is recomputed from staged put-away tasks. Outbound backlog is the
        // demand accumulated by stage 2. Every recompute clamps at zero and only emits an event on change.
        int receivingBacklog = ReceivingBacklog(await _tasks.GetUnassignedAsync(ct).ConfigureAwait(false));

        var outboundChanged = _metrics.SetOutbound(outboundDemand, now);
        if (outboundChanged is not null)
        {
            events.Add(outboundChanged);
        }

        var receivingChanged = _metrics.SetReceiving(receivingBacklog, now);
        if (receivingChanged is not null)
        {
            events.Add(receivingChanged);
        }

        // ---- Persist all mutations atomically, then publish all collected events (Req 27.3, 27.4). ----
        if (mutatedLots.Count > 0 || mutatedAgents.Count > 0 || mutatedStarships.Count > 0 || mutatedTasks.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        foreach (var @event in events)
        {
            await _eventBus.PublishAsync(@event, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    /// <summary>
    /// Stage 2 helper: the deterministic outbound demand contributed by colony orders whose delivery
    /// window opened during this tick's window <c>(now − delta, now]</c> (Req 12.2). Trend-boundary
    /// effects are already baked into the order lines by the driver's demand generator (Req 12.3); this
    /// core stage only sums the line quantities of the orders that became active this tick, so the effect
    /// is a pure function of the orders + <c>(now, delta)</c> and generates nothing (Req 1.8).
    /// </summary>
    private static int OrderIntakeDemand(
        IReadOnlyList<ColonyOrder> orders, DateTimeOffset now, TimeSpan delta)
    {
        var windowStart = now - delta;
        long total = 0;

        foreach (var order in orders)
        {
            // "Received this tick": the delivery window opened within (now − delta, now].
            if (order.DeliveryWindowStart > windowStart && order.DeliveryWindowStart <= now)
            {
                foreach (var line in order.Lines)
                {
                    total += line.Quantity;
                }
            }
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// Stage 5 helper: the receiving backlog as the number of unassigned put-away tasks — arrivals
    /// received but not yet absorbed by put-away/capacity (Req 14.3). Non-negative by construction.
    /// </summary>
    private static int ReceivingBacklog(IReadOnlyList<WarehouseTask> unassigned)
    {
        int count = 0;
        foreach (var task in unassigned)
        {
            if (task.Type == WarehouseTaskType.PutAway)
            {
                count++;
            }
        }

        return count;
    }
}
