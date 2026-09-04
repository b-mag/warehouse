using Forge.Application.Abstractions;

using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;

namespace Forge.Application.Simulation;

/// <summary>
/// The five fixed-order per-tick RULE stages the <see cref="ApplyTickRulesHandler"/> runs (design.md
/// "Core rule application", Req 10.4). Each stage is a <b>pure, deterministic function</b> of the
/// state it is handed plus the current simulated time / delta: no clock access, no wall time, no RNG,
/// and no input generation (Req 1.8). A stage mutates the domain aggregates it is given (marking lots
/// expired, moving agents, loading starships) and returns the ordered domain events it produced for the
/// handler to persist and publish. Stages never read "now" from a clock — the handler supplies the
/// simulated timestamp — so identical <c>(state, now, delta)</c> always yields the identical outcome
/// (reproducibility — Req 19.6).
/// <para>
/// Splitting the pipeline into these stateless stage functions keeps the large orchestration readable
/// and each rule independently testable, while the handler owns the fixed ordering, repository loads,
/// persistence, and publication.
/// </para>
/// </summary>
internal static partial class TickStages
{
    /// <summary>
    /// <b>Stage 1 — Expiry decay (Req 4).</b> For each supplied lot, ordered by <see cref="GelLot.Id"/>
    /// for a deterministic pass, invoke the pure domain rule
    /// <see cref="GelLot.TryExpireAt(DateTimeOffset, out LotExpired?)"/> at <paramref name="now"/>.
    /// A lot that transitions non-expired → expired is added to <paramref name="mutatedLots"/> (so the
    /// caller stages exactly the changed lots for update) and its single <see cref="LotExpired"/> event
    /// is collected. Already-expired lots and lots with remaining whole-second shelf-life are untouched
    /// and raise nothing (idempotent — Req 4.4). Applies zero effect when the pass finds nothing to
    /// transition, e.g. a paused tick reusing the same <paramref name="now"/> (Req 10.4).
    /// </summary>
    /// <param name="lots">Every lot to evaluate; the caller loads these from the lot repository.</param>
    /// <param name="now">The current simulated time expiry is evaluated against.</param>
    /// <param name="mutatedLots">Receives the lots that transitioned to expired this tick.</param>
    /// <returns>The <see cref="LotExpired"/> events to publish, one per transition, in lot-id order.</returns>
    public static IReadOnlyList<IDomainEvent> ExpiryDecay(
        IReadOnlyList<GelLot> lots,
        DateTimeOffset now,
        List<GelLot> mutatedLots)
    {
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(mutatedLots);

        var events = new List<IDomainEvent>();

        foreach (var lot in Ordered(lots, l => l.Id))
        {
            if (lot.TryExpireAt(now, out var expired) && expired is not null)
            {
                mutatedLots.Add(lot);
                events.Add(expired);
            }
        }

        return events;
    }

    /// <summary>
    /// <b>Stage 3 — Agent movement (Req 18.4, 18.6, 19.2).</b> Advance each agent along its currently
    /// assigned <see cref="Path"/> by <c>speed × delta</c>, reserving each path segment for the interval
    /// the agent occupies it before it moves, and resolving contention deterministically.
    /// <list type="bullet">
    ///   <item><description>
    ///     Agents are processed in ascending <see cref="Agent.Id"/> order so the <b>lower-id agent wins</b>
    ///     a contested segment and the higher-id agent re-plans or holds (Req 19.6).
    ///   </description></item>
    ///   <item><description>
    ///     An agent whose assigned path is unroutable — no traversable path from its position to the
    ///     path's destination — yields an <see cref="UnroutableTask"/> event and does not move (Req 18.6);
    ///     the <paramref name="taskForAgent"/> resolver maps the agent to the task id the event reports.
    ///   </description></item>
    ///   <item><description>
    ///     When the segments the agent would enter this tick cannot be reserved (a lower-id agent holds an
    ///     overlapping interval), the agent is <b>held</b>: it does not move and no reservation is taken,
    ///     leaving it to retry next tick (Req 19.2). Re-planning around the conflict is attempted first via
    ///     the planner; a hold is the fallback when no alternative exists.
    ///   </description></item>
    /// </list>
    /// Applies zero effect when <paramref name="delta"/> is non-positive (Req 10.4) — a paused tick moves
    /// no agent and reserves nothing.
    /// </summary>
    /// <param name="state">The grid, agents, and shared reservation ledger for this tick.</param>
    /// <param name="planner">The deterministic path planner (re-planning + unroutable detection).</param>
    /// <param name="now">The simulated time the tick starts at (segment intervals begin here).</param>
    /// <param name="delta">The simulated duration to advance; non-positive is a no-op.</param>
    /// <param name="mutatedAgents">Receives the agents whose position changed this tick.</param>
    /// <param name="taskForAgent">
    /// Resolves the <see cref="WarehouseTaskId"/> an <see cref="UnroutableTask"/> event should report for
    /// a given agent, or <see langword="null"/> when the agent is not executing a resolvable task.
    /// </param>
    /// <returns>The domain events produced (currently <see cref="UnroutableTask"/>), in agent-id order.</returns>
    public static IReadOnlyList<IDomainEvent> AgentMovement(
        TickState state,
        IPathPlanner planner,
        DateTimeOffset now,
        TimeSpan delta,
        List<Agent> mutatedAgents,
        Func<Agent, WarehouseTaskId?> taskForAgent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(mutatedAgents);
        ArgumentNullException.ThrowIfNull(taskForAgent);

        var events = new List<IDomainEvent>();

        // Req 10.4: a paused / non-advancing tick moves nothing and reserves nothing.
        if (delta <= TimeSpan.Zero)
        {
            return events;
        }

        // Reservations are intra-tick coordination only (they stop two agents sharing a segment DURING
        // this tick). Clear the ledger at the start of each movement pass so it does not accumulate every
        // agent's segments across every tick — unbounded growth that slows the conflict scan and can
        // cause spurious cross-tick contention that freezes agents in place.
        state.Ledger.Clear();

        foreach (var agent in Ordered(state.Agents, a => a.Id))
        {
            var path = agent.CurrentPath;

            // Agents that are executing a task (they hold an entry in the agent->task link) must NOT be
            // patrol-dispatched: they follow the path the task-execution stage assigned toward their
            // task's work cell, and once they arrive they HOLD position so the task-execution stage can
            // detect arrival and complete the task next. Overwriting their path with a patrol route here
            // was making task-bound agents wander and never complete (backlog never drained).
            bool hasTask = state.AgentTasks.ContainsKey(agent.Id);

            // Dispatch: idle TASKLESS agents return to the staging bay and wait there (no random patrol).
            // Destination is a deterministic bay slot from agent id (Req 19.6).
            if (!hasTask && (path is null || path.StepCount == 0 || agent.Position == path.Cells[^1]))
            {
                if (VisualGridLayout.IsInIdleBay(agent.Position))
                {
                    agent.ClearPath();
                    continue; // resting in the bay until TaskExecution assigns work
                }

                var target = NextDestination(state.Grid, agent);
                if (target is not { } dest || dest == agent.Position)
                {
                    continue;
                }

                var dispatchPlan = planner.Plan(state.Grid, agent.Position, dest);
                if (dispatchPlan.IsUnroutable)
                {
                    var unroutableTaskId = taskForAgent(agent);
                    if (unroutableTaskId is { } uid)
                    {
                        events.Add(new UnroutableTask(
                            uid, agent.Position.X, agent.Position.Y, dest.X, dest.Y, now));
                    }

                    continue;
                }

                agent.AssignPath(dispatchPlan.Path);
                path = dispatchPlan.Path;
            }

            var destination = path.Cells[^1];

            // Req 18.6: if no traversable path exists from where the agent stands to its destination,
            // report the task unroutable and leave the agent in place (no move, no reservation).
            var plan = planner.Plan(state.Grid, agent.Position, destination);
            if (plan.IsUnroutable)
            {
                var taskId = taskForAgent(agent);
                if (taskId is { } id)
                {
                    events.Add(new UnroutableTask(
                        id, agent.Position.X, agent.Position.Y, destination.X, destination.Y, now));
                }

                continue;
            }

            // Follow the freshly-planned route so re-planning around obstacles is honoured (Req 19.2).
            var route = plan.Path;
            if (route.StepCount == 0)
            {
                continue; // already at destination
            }

            // How many whole cells the agent can traverse in this delta (Req 18.4: speed × delta).
            int reachable = (int)Math.Floor(agent.CellsPerSecond * delta.TotalSeconds);
            if (reachable <= 0)
            {
                continue; // not enough time to clear a whole segment this tick
            }

            int steps = Math.Min(reachable, route.StepCount);

            // Build the timed segments the agent would occupy this tick, one contiguous [enter, exit)
            // interval per step at the agent's speed. Reserve them as an all-or-nothing batch so a
            // conflict on any segment holds the agent (Req 19.1, 19.2).
            var timed = BuildTimedSegments(route, steps, agent.CellsPerSecond, now);
            var outcome = state.Ledger.TryReserve(agent.Id, timed);
            if (!outcome.IsGranted)
            {
                // Contention: a lower-id agent holds an overlapping interval. Hold this (higher-id)
                // agent — no move, no reservation — to retry next tick (Req 19.2, 19.6).
                continue;
            }

            // Advance the agent to the last reserved cell (single-cell occupancy preserved — Req 18.2).
            agent.MoveTo(route.Cells[steps]);
            // Keep CurrentPath as the remaining route from the new cell so clients never receive
            // stale "past" cells (which looked like teleports when prepending agent.x/y).
            var remaining = new Cell[route.Cells.Count - steps];
            for (int i = 0; i < remaining.Length; i++)
            {
                remaining[i] = route.Cells[steps + i];
            }

            agent.AssignPath(new Forge.Domain.Spatial.Path(remaining));
            mutatedAgents.Add(agent);
        }

        return events;
    }

    /// <summary>
    /// <b>Task-execution stage — make agents actually work the backlog.</b> Assigns queued/created
    /// <see cref="WarehouseTaskType.PutAway"/> and <see cref="WarehouseTaskType.Pick"/> tasks to idle
    /// agents, routes each assigned agent to its task's destination cell, and completes a task when its
    /// agent has arrived — draining the receiving (PutAway) / outbound (Pick) backlog over time.
    /// <para>
    /// This is what turns the WORKERS-ON-SHIFT and ARRIVAL-RATE operator controls into a real feedback
    /// loop: more agents assigned in parallel drain the backlog faster; a higher arrival rate refills it.
    /// The agent-&gt;task link lives in <see cref="TickState.AgentTasks"/> (in-memory, tick-scoped) so an
    /// agent stays bound to its task across ticks until it arrives.
    /// </para>
    /// <para><b>Determinism (Req 19.6).</b> Agents are processed in ascending <see cref="Agent.Id"/> order
    /// and unassigned tasks in ascending <see cref="WarehouseTask.Id"/> order, so the same
    /// <c>(agents, tasks, positions)</c> always produces the same assignments and completions. No clock,
    /// no RNG. A non-positive <paramref name="delta"/> is a no-op (Req 10.4).</para>
    /// <para><b>Boundary.</b> The stage generates no tasks (that is the driver / order + inbound handlers'
    /// job — Req 1.8); it only advances the lifecycle of tasks that already exist and moves agents that
    /// already exist. Task status/assignment mutations go through the guarded domain transitions on
    /// <see cref="WarehouseTask"/>; the caller stages the mutated tasks for the atomic unit-of-work commit
    /// and publishes the returned completion events after the commit succeeds.</para>
    /// </summary>
    /// <param name="state">The tick state: agents, grid, and the live agent-&gt;task link map.</param>
    /// <param name="unassigned">Tasks awaiting assignment (created/queued), from the task repository.</param>
    /// <param name="tasksById">Resolver for a task by id (to complete an agent's in-flight task).</param>
    /// <param name="planner">The deterministic path planner used to route an agent to its task.</param>
    /// <param name="maxActiveAgents">
    /// Upper bound on how many agents may hold a task at once — driven by WORKERS ON SHIFT so the operator
    /// control governs parallelism. A non-positive value means "no cap".
    /// </param>
    /// <param name="now">The current simulated time (stamped on completion events).</param>
    /// <param name="delta">The tick duration; non-positive is a no-op.</param>
    /// <param name="mutatedTasks">Receives tasks whose state changed this tick (assigned/started/completed).</param>
    /// <param name="mutatedAgents">Receives agents given a fresh path toward a newly assigned task.</param>
    /// <returns>The completed-task outcome: the <see cref="TaskCompleted"/> events plus per-type counts.</returns>
    public static TaskExecutionOutcome TaskExecution(
        TickState state,
        IReadOnlyList<WarehouseTask> unassigned,
        Func<WarehouseTaskId, WarehouseTask?> tasksById,
        IPathPlanner planner,
        int maxActiveAgents,
        DateTimeOffset now,
        TimeSpan delta,
        List<WarehouseTask> mutatedTasks,
        List<Agent> mutatedAgents,
        IReadOnlyList<GelLot> lots,
        IReadOnlyList<Guid> orderedZoneIds)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(unassigned);
        ArgumentNullException.ThrowIfNull(tasksById);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(mutatedTasks);
        ArgumentNullException.ThrowIfNull(mutatedAgents);
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(orderedZoneIds);

        var events = new List<IDomainEvent>();
        int putAwayCompleted = 0;
        int pickCompleted = 0;
        int assigned = 0;
        int skippedUnroutable = 0;
        int skippedAssignFailed = 0;
        int inFlightNotArrived = 0;
        var completedPutAwayLots = new List<GelLotId>();
        var pickedUpLots = new List<GelLotId>();

        if (delta <= TimeSpan.Zero)
        {
            return new TaskExecutionOutcome(
                events, putAwayCompleted, pickCompleted, 0, 0, 0, 0, 0,
                completedPutAwayLots, pickedUpLots);
        }

        var link = state.AgentTasks;
        var agents = Ordered(state.Agents, a => a.Id);

        // ---- Phase A: complete in-flight tasks whose agent has reached the destination cell. ----
        foreach (var agent in agents)
        {
            if (!link.TryGetValue(agent.Id, out var taskId))
            {
                continue;
            }

            var task = tasksById(taskId);
            if (task is null || task.Status == Forge.Domain.Tasks.TaskStatus.Completed)
            {
                // The task vanished or is already done — release the agent so it can take new work.
                link.TryRemove(agent.Id, out _);
                agent.ClearPath();
                continue;
            }

            var workCell = WorkCellFor(state, task, lots, orderedZoneIds);
            if (agent.Position != workCell)
            {
                inFlightNotArrived++;
                continue; // still travelling; AgentMovement advances it toward the work cell.
            }

            // PutAway leg 1: arrived at the train — pick up the pallet, then route to the zone face.
            if (task.Type == WarehouseTaskType.PutAway &&
                state.PutAwayTaskLotLinks.TryGetValue(task.Id, out var pickupLot) &&
                IsLotInList(state.InboundQueueLotIds, pickupLot))
            {
                var inbound = state.InboundQueueLotIds;
                RemoveLotFromList(ref inbound, pickupLot);
                state.InboundQueueLotIds = inbound;

                var transit = state.InTransitLotIds;
                AppendLotToList(ref transit, pickupLot);
                state.InTransitLotIds = transit;

                var zoneCell = PutAwayZoneCell(state.Grid, task);
                var toZone = planner.Plan(state.Grid, agent.Position, zoneCell);
                if (!toZone.IsUnroutable)
                {
                    agent.AssignPath(toZone.Path);
                    mutatedAgents.Add(agent);
                }

                inFlightNotArrived++;
                continue;
            }

            // Pick leg 1: arrived at a holding zone — grab stored cargo, then route to the ship dock.
            if (task.Type == WarehouseTaskType.Pick &&
                !state.PickTaskLotLinks.ContainsKey(task.Id))
            {
                var lot = SelectPickableLot(lots, state, task);
                if (lot is null)
                {
                    // Nothing left in storage — release and let another tick retry.
                    link.TryRemove(agent.Id, out _);
                    agent.ClearPath();
                    continue;
                }

                state.PickTaskLotLinks[task.Id] = lot.Id;
                var transit = state.InTransitLotIds;
                AppendLotToList(ref transit, lot.Id);
                state.InTransitLotIds = transit;
                pickedUpLots.Add(lot.Id);

                var dockCell = PickDockCell(state.Grid, task);
                var toDock = planner.Plan(state.Grid, agent.Position, dockCell);
                if (!toDock.IsUnroutable)
                {
                    agent.AssignPath(toDock.Path);
                    mutatedAgents.Add(agent);
                }

                inFlightNotArrived++;
                continue;
            }

            // Arrived: drive the guarded transition to completion. Start() first if still Assigned.
            if (task.Status == Forge.Domain.Tasks.TaskStatus.Assigned)
            {
                task.Start();
            }

            if (task.Status == Forge.Domain.Tasks.TaskStatus.InProgress && task.Complete().IsSuccess)
            {
                mutatedTasks.Add(task);
                events.Add(new TaskCompleted(task.Id, agent.Worker, 0m, now));
                if (task.Type == WarehouseTaskType.PutAway)
                {
                    putAwayCompleted++;

                    if (state.PutAwayTaskLotLinks.TryGetValue(task.Id, out var lotId))
                    {
                        completedPutAwayLots.Add(lotId);
                        var transit = state.InTransitLotIds;
                        RemoveLotFromList(ref transit, lotId);
                        state.InTransitLotIds = transit;
                    }
                }
                else if (task.Type == WarehouseTaskType.Pick)
                {
                    // Only load the ship after the worker carried cargo from a zone to the dock.
                    if (state.PickTaskLotLinks.TryGetValue(task.Id, out var carriedLot))
                    {
                        pickCompleted++;

                        foreach (var ship in Ordered(state.Starships, s => s.Id))
                        {
                            if (!CanLoadStarship(state, ship) || ship.RemainingCapacity <= 0)
                            {
                                continue;
                            }

                            if (ship.TryLoad(VisualSimulationConstants.MaxPalletsLoadedPerTick).IsSuccess)
                            {
                                break;
                            }
                        }

                        var transit = state.InTransitLotIds;
                        RemoveLotFromList(ref transit, carriedLot);
                        state.InTransitLotIds = transit;
                        state.PickTaskLotLinks.TryRemove(task.Id, out _);
                    }
                }
            }

            // Whether completed or rejected, release the agent + link so it can be re-dispatched.
            link.TryRemove(agent.Id, out _);
            agent.ClearPath();
        }

        // ---- Phase B: assign fresh work to idle agents, up to the active cap. ----
        // PutAway first so inbound never starves behind unfulfillable Picks (no stored inventory yet).
        // Only PutAway / Pick tasks are executed here (Load/CycleCount/TempCheck are out of scope).
        var queue = new Queue<WarehouseTask>(
            Ordered(unassigned, t => t.Id)
                .Where(t => t.Type is WarehouseTaskType.PutAway or WarehouseTaskType.Pick)
                .OrderBy(t => t.Type == WarehouseTaskType.PutAway ? 0 : 1)
                .ThenBy(t => t.Id));

        foreach (var agent in agents)
        {
            if (queue.Count == 0)
            {
                break;
            }

            if (link.ContainsKey(agent.Id))
            {
                continue; // already executing a task
            }

            if (maxActiveAgents > 0 && link.Count >= maxActiveAgents)
            {
                break; // operator's WORKERS-ON-SHIFT cap reached for this tick
            }

            // Keep pulling until this agent gets a workable task (or the queue is empty).
            // Previously a Pick with no stored inventory burned the agent AND discarded the task,
            // so PutAways later in the queue never ran and workers sat in the breakroom forever.
            while (queue.Count > 0)
            {
                var task = queue.Dequeue();

                if (task.Type == WarehouseTaskType.Pick &&
                    SelectPickableLot(lots, state, task) is null)
                {
                    skippedAssignFailed++;
                    continue; // try next task for the same agent
                }

                var workCell = WorkCellFor(state, task, lots, orderedZoneIds);
                var plan = planner.Plan(state.Grid, agent.Position, workCell);
                if (plan.IsUnroutable)
                {
                    skippedUnroutable++;
                    continue;
                }

                if (task.AssignTo(agent.Worker).IsFailure)
                {
                    skippedAssignFailed++;
                    continue;
                }

                agent.AssignPath(plan.Path);
                link[agent.Id] = task.Id;
                mutatedTasks.Add(task);
                mutatedAgents.Add(agent);
                assigned++;
                break; // this agent is busy
            }
        }

        return new TaskExecutionOutcome(
            events, putAwayCompleted, pickCompleted,
            assigned, skippedUnroutable, skippedAssignFailed, inFlightNotArrived, queue.Count,
            completedPutAwayLots, pickedUpLots);
    }

    /// <summary>
    /// Build the ordered <see cref="TimedSegment"/> batch for the first <paramref name="steps"/> steps of
    /// <paramref name="route"/>, each occupied for <c>1 / cellsPerSecond</c> simulated seconds starting at
    /// <paramref name="start"/>. Contiguous half-open intervals (<c>[enter, exit)</c>) so a hand-off at a
    /// shared cell does not self-conflict, matching <see cref="TimedSegment"/>'s overlap semantics.
    /// </summary>
    private static IReadOnlyList<TimedSegment> BuildTimedSegments(
        Domain.Spatial.Path route, int steps, double cellsPerSecond, DateTimeOffset start)
    {
        var perStep = TimeSpan.FromSeconds(1d / cellsPerSecond);
        var segments = new List<TimedSegment>(steps);
        var enter = start;

        var cells = route.Cells;
        for (int i = 0; i < steps; i++)
        {
            var exit = enter + perStep;
            segments.Add(new TimedSegment(new PathSegment(cells[i], cells[i + 1]), enter, exit));
            enter = exit;
        }

        return segments;
    }

    /// <summary>
    /// <b>Stage 4 — Starship loading (Req 13.2).</b> For each starship, ordered by
    /// <see cref="Starship.Id"/>, that is within an open loading window at <paramref name="now"/>, run
    /// FEFO pick+load via <paramref name="loadingService"/> for the demand resolved by
    /// <paramref name="demandFor"/>. A window that has closed (was open at <c>now − delta</c> but is not
    /// open now) yields a <see cref="LoadingWindowClosed"/> event reporting loaded quantity and shortfall
    /// (Req 13.6). Applies zero effect when <paramref name="delta"/> is non-positive (Req 10.4).
    /// <para>
    /// The service handles window admission, FEFO ordering, and capacity internally; a rejected load
    /// leaves the starship's loaded quantity unchanged. Loading draws from <paramref name="lots"/> — the
    /// caller's already-loaded inventory — so this stage generates no inputs (Req 1.8).
    /// </para>
    /// </summary>
    /// <param name="state">The starships eligible for loading this tick.</param>
    /// <param name="loadingService">The window-admitted FEFO loading rule (task 21.1).</param>
    /// <param name="lots">The current inventory FEFO selection draws from.</param>
    /// <param name="now">The current simulated time (window admission + FEFO cutoff).</param>
    /// <param name="delta">The simulated duration of this tick; non-positive is a no-op.</param>
    /// <param name="mutatedStarships">Receives starships whose loaded quantity changed this tick.</param>
    /// <param name="demandFor">
    /// Resolves the <c>(gel type, requested quantity)</c> a starship should load this tick, or
    /// <see langword="null"/> when it has nothing to load. Keeps the stage free of demand generation.
    /// </param>
    /// <returns>The <see cref="LoadingWindowClosed"/> events produced, in starship-id order.</returns>
    public static IReadOnlyList<IDomainEvent> StarshipLoading(
        TickState state,
        Loading.StarshipLoadingService loadingService,
        IReadOnlyList<GelLot> lots,
        DateTimeOffset now,
        TimeSpan delta,
        List<Starship> mutatedStarships,
        Func<Starship, (GelTypeId GelType, int Quantity)?> demandFor)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(loadingService);
        ArgumentNullException.ThrowIfNull(lots);
        ArgumentNullException.ThrowIfNull(mutatedStarships);
        ArgumentNullException.ThrowIfNull(demandFor);

        var events = new List<IDomainEvent>();

        // Req 10.4: a paused tick loads nothing and closes no window.
        if (delta <= TimeSpan.Zero)
        {
            return events;
        }

        var previously = now - delta;

        foreach (var starship in Ordered(state.Starships, s => s.Id))
        {
            var demand = demandFor(starship);

            // Req 13.2: within an open window, attempt a FEFO-ordered, capacity-checked load.
            if (starship.IsWithinAnyWindow(now) && demand is { } d && d.Quantity > 0)
            {
                var before = starship.LoadedQuantity;
                var load = loadingService.TryLoad(starship, d.GelType, d.Quantity, lots, now);
                if (load.IsSuccess && starship.LoadedQuantity != before)
                {
                    mutatedStarships.Add(starship);
                }
            }

            // Req 13.6: a window that was open last tick but is not open now has closed — report loaded +
            // shortfall. Requested-vs-loaded here reports the tick's demand shortfall (zero when none).
            bool wasOpen = starship.IsWithinAnyWindow(previously);
            bool isOpen = starship.IsWithinAnyWindow(now);
            if (wasOpen && !isOpen)
            {
                int requested = demand is { } dd ? dd.Quantity : starship.LoadedQuantity;
                events.Add(loadingService.CloseWindow(
                    starship.Id, requested, starship.LoadedQuantity, now));
            }
        }

        return events;
    }

    /// <summary>
    /// Idle workers walk to a deterministic staging-bay slot (Req 19.6). Returns null on a
    /// degenerate grid.
    /// </summary>
    private static Cell? NextDestination(WarehouseGrid grid, Agent agent)
    {
        if (grid.Width <= 0 || grid.Height <= 0)
        {
            return null;
        }

        var bay = VisualGridLayout.IdleBaySlot(grid, agent);
        return bay == agent.Position ? null : bay;
    }

    /// <summary>
    /// The effective grid work cell an agent travels to for <paramref name="task"/>.
    /// PutAway is two-legged: rail pickup first (while lot is still inbound), then zone face.
    /// Pick is two-legged: holding-zone grab first, then ship dock.
    /// </summary>
    private static Cell WorkCellFor(
        TickState state,
        WarehouseTask task,
        IReadOnlyList<GelLot> lots,
        IReadOnlyList<Guid> orderedZoneIds)
    {
        var grid = state.Grid;
        if (grid.Width <= 0 || grid.Height <= 0)
        {
            return task.Destination;
        }

        if (task.Type == WarehouseTaskType.PutAway &&
            state.PutAwayTaskLotLinks.TryGetValue(task.Id, out var lotId) &&
            IsLotInList(state.InboundQueueLotIds, lotId))
        {
            return VisualGridLayout.ReceivingPickupCell(grid);
        }

        if (task.Type == WarehouseTaskType.PutAway)
        {
            return PutAwayZoneCell(grid, task);
        }

        if (task.Type == WarehouseTaskType.Pick)
        {
            if (state.PickTaskLotLinks.ContainsKey(task.Id))
            {
                return PickDockCell(grid, task);
            }

            var lot = SelectPickableLot(lots, state, task);
            if (lot?.AssignedZoneId is { } zoneId)
            {
                return VisualGridLayout.ZoneEntryCellForId(zoneId.Value, orderedZoneIds, grid);
            }

            // Fallback: a deterministic zone face until inventory appears.
            uint fold = StableGuidFold(task.Id.Value);
            int zoneCount = Math.Max(1, orderedZoneIds.Count);
            return VisualGridLayout.ZoneEntryCell((int)(fold % (uint)zoneCount), zoneCount, grid);
        }

        var dest = task.Destination;
        bool inBounds = dest.X >= 0 && dest.X < grid.Width && dest.Y >= 0 && dest.Y < grid.Height;
        bool degenerate = dest is { X: 0, Y: 0 };

        if (inBounds && !degenerate)
        {
            if (!grid.IsTraversable(dest))
            {
                return NearestTraversable(grid, dest) ?? VisualGridLayout.ReceivingPickupCell(grid);
            }

            return dest;
        }

        uint fxFold = StableGuidFold(task.Id.Value);
        int fx = (int)(fxFold % (uint)grid.Width);
        int fy = (int)((fxFold / (uint)grid.Width) % (uint)grid.Height);
        return new Cell(fx, fy);
    }

    private static Cell PickDockCell(WarehouseGrid grid, WarehouseTask task)
    {
        uint fold = StableGuidFold(task.Id.Value);
        int berthCount = Math.Max(1, Math.Min(4, grid.Width / 6));
        int berth = (int)(fold % (uint)berthCount);
        int spacing = Math.Max(1, grid.Width / (berthCount + 1));
        int x = Math.Clamp(spacing * (berth + 1), 0, grid.Width - 1);
        int y = grid.Height > 0 ? grid.Height - 1 : 0;
        var cell = new Cell(x, y);
        if (grid.IsTraversable(cell))
        {
            return cell;
        }

        return NearestTraversable(grid, cell) ?? cell;
    }

    /// <summary>
    /// FEFO-ish pickable lot already sitting in a holding zone (not inbound, not already carried).
    /// </summary>
    private static GelLot? SelectPickableLot(
        IReadOnlyList<GelLot> lots,
        TickState state,
        WarehouseTask _)
    {
        // Lots already claimed by another in-flight pick.
        var claimed = new HashSet<GelLotId>();
        foreach (var kv in state.PickTaskLotLinks)
        {
            claimed.Add(kv.Value);
        }

        GelLot? best = null;
        foreach (var lot in lots)
        {
            if (lot.IsExpired || lot.Quantity <= 0 || lot.AssignedZoneId is null)
            {
                continue;
            }

            if (claimed.Contains(lot.Id) || IsLotInList(state.InTransitLotIds, lot.Id) ||
                IsLotInList(state.InboundQueueLotIds, lot.Id))
            {
                continue;
            }

        // Prefer earliest expiry, then FEFO priority, then lot id (Req 19.6).
        if (best is null ||
            lot.ExpiresAt < best.ExpiresAt ||
            (lot.ExpiresAt == best.ExpiresAt && lot.FefoPriority < best.FefoPriority) ||
            (lot.ExpiresAt == best.ExpiresAt && lot.FefoPriority == best.FefoPriority &&
             lot.Id.Value.CompareTo(best.Id.Value) < 0))
        {
            best = lot;
        }
      }

      return best;
    }

    private static Cell PutAwayZoneCell(WarehouseGrid grid, WarehouseTask task)
    {
        var dest = task.Destination;
        bool inBounds = dest.X >= 0 && dest.X < grid.Width && dest.Y >= 0 && dest.Y < grid.Height;
        bool degenerate = dest is { X: 0, Y: 0 };

        if (inBounds && !degenerate)
        {
            if (grid.IsTraversable(dest))
            {
                return dest;
            }

            return NearestTraversable(grid, dest) ?? VisualGridLayout.ReceivingPickupCell(grid);
        }

        uint fold = StableGuidFold(task.Id.Value);
        int zoneCount = 6;
        int zoneIndex = (int)(fold % (uint)zoneCount);
        return VisualGridLayout.ZoneEntryCell(zoneIndex, zoneCount, grid);
    }

    private static Cell? NearestTraversable(WarehouseGrid grid, Cell around)
    {
        for (int r = 0; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                    {
                        continue;
                    }

                    var c = new Cell(around.X + dx, around.Y + dy);
                    if (grid.IsTraversable(c))
                    {
                        return c;
                    }
                }
            }
        }

        return null;
    }

    private static bool IsLotInList(IReadOnlyList<GelLotId> list, GelLotId lotId)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(lotId))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveLotFromList(ref IReadOnlyList<GelLotId> list, GelLotId lotId)
    {
        int nextCount = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Equals(lotId))
            {
                nextCount++;
            }
        }

        if (nextCount == list.Count)
        {
            return;
        }

        var next = new GelLotId[nextCount];
        int k = 0;
        for (int i = 0; i < list.Count; i++)
        {
            if (!list[i].Equals(lotId))
            {
                next[k++] = list[i];
            }
        }

        list = next;
    }

    private static void AppendLotToList(ref IReadOnlyList<GelLotId> list, GelLotId lotId)
    {
        if (IsLotInList(list, lotId))
        {
            return;
        }

        var next = new GelLotId[list.Count + 1];
        for (int i = 0; i < list.Count; i++)
        {
            next[i] = list[i];
        }

        next[list.Count] = lotId;
        list = next;
    }

    /// <summary>
    /// A stable, process-independent unsigned fold of a <see cref="Guid"/> (FNV-1a over its 16 bytes).
    /// Used instead of <see cref="object.GetHashCode"/> so dispatch is reproducible across processes,
    /// matching the seeding/determinism convention used elsewhere in the solution.
    /// </summary>
    private static uint StableGuidFold(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        uint hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return hash;
    }

    /// <summary>
    /// Deterministically order a sequence by a comparable key so a stage's iteration never depends on
    /// the caller's storage/enumeration order (Req 19.6). Materialized so callers can enumerate freely.
    /// </summary>
    private static IReadOnlyList<T> Ordered<T, TKey>(IEnumerable<T> source, Func<T, TKey> key)
        where TKey : IComparable<TKey>
    {
        var list = new List<T>(source);
        list.Sort((a, b) => key(a).CompareTo(key(b)));
        return list;
    }
}

