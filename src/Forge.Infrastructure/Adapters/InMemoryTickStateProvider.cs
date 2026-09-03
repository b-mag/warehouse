using Forge.Application.Simulation;
using Forge.Domain.Gels;
using Forge.Domain.Common;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// The Phase-1 in-memory spatial/vessel store behind the Application <see cref="ITickStateProvider"/>
/// seam (task 33.3; see <see cref="TickState"/> for why this is a distinct seam and not a repository).
/// It holds the <em>live</em> <see cref="TickState"/> — the <see cref="WarehouseGrid"/> agents move on,
/// the movement <see cref="Agent"/>s, the shared segment <see cref="ReservationLedger"/>, and the
/// <see cref="Starship"/>s being loaded — as a single singleton so the per-tick movement (stage 3) and
/// starship-loading (stage 4) rule stages can mutate agents/starships in place and the snapshot query
/// can render them (Req 18, 13).
/// <para>
/// <b>Why in-memory for Phase 1.</b> There is no agent / starship / grid persistence yet (the repository
/// seams cover gel lots, orders, tasks, workers, zones, and colonies only). Rather than leave the
/// spatial subsystem dark — which would make the movement and loading stages permanent no-ops and the
/// demo motionless — this provider synthesizes a reasonable initial grid plus a handful of agents and
/// starships at startup (<see cref="Initialize"/>), so the warehouse visibly comes alive. When agent /
/// starship / grid persistence lands, an EF-backed provider slots behind this same seam with no change
/// to the handler or the snapshot query.
/// </para>
/// <para>
/// <b>Determinism.</b> The initial grid, agents, and starships are derived from an explicit seed plus
/// stable ordinals (never <see cref="Guid.NewGuid"/>), so an identical seed reproduces an identical
/// initial spatial state. The provider holds no clock and no RNG beyond that seeded construction; the
/// tick handler orders agents/starships by id before iterating, so the stored order is immaterial.
/// </para>
/// <para>
/// <b>Thread-safety.</b> The tick loop mutates the held agents/starships while a snapshot read may
/// project them concurrently, so <see cref="GetTickStateAsync"/> returns the held state under a lock.
/// The returned <see cref="TickState"/> exposes the same live agent/starship instances the tick stages
/// mutate (by design — the stages advance them in place), so callers must not assume an immutable copy.
/// </para>
/// </summary>
public sealed class InMemoryTickStateProvider : ITickStateProvider
{
    private readonly object _gate = new();
    private TickState? _state;

    private int _seed;
    private int _gridWidth = 32;
    private int _gridHeight = 32;

    // Pool of all synthesized agents. We dynamically project which agents are "active" into
    // TickState.Agents based on the operator's WorkersOnShift control.
    private IReadOnlyList<Agent> _agentPool = Array.Empty<Agent>();
    private Dictionary<AgentId, int> _agentPoolIndexById = new();

    /// <summary>
    /// Whether an initial spatial state has been installed. Before <see cref="Initialize"/> the
    /// provider returns <see langword="null"/>, making the movement + loading stages deterministic
    /// no-ops (a valid headless / cold-chain-only run).
    /// </summary>
    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _state is not null;
            }
        }
    }

    /// <inheritdoc />
    public Task<TickState?> GetTickStateAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_state);
        }
    }

    /// <inheritdoc />
    public void EnqueueInboundPutAway(GelLotId lotId, WarehouseTaskId putAwayTaskId)
    {
        ArgumentNullException.ThrowIfNull(lotId);
        ArgumentNullException.ThrowIfNull(putAwayTaskId);

        lock (_gate)
        {
            if (_state is null)
            {
                return;
            }

            _state.PutAwayTaskLotLinks.TryAdd(putAwayTaskId, lotId);

            // Queue ordering matters for the later "train" visuals; preserve insertion order.
            var current = _state.InboundQueueLotIds;
            bool alreadyQueued = false;
            for (var i = 0; i < current.Count; i++)
            {
                if (current[i].Equals(lotId))
                {
                    alreadyQueued = true;
                    break;
                }
            }

            if (!alreadyQueued)
            {
                var next = new GelLotId[current.Count + 1];
                for (var i = 0; i < current.Count; i++)
                {
                    next[i] = current[i];
                }
                next[current.Count] = lotId;
                _state.InboundQueueLotIds = next;
            }
        }
    }

    public void ApplyWorkerCount(int workersOnShift)
    {
        lock (_gate)
        {
            if (_state is null)
            {
                return;
            }

            // Negative values have no meaning; operator validation should prevent it anyway.
            int desired = Math.Max(0, workersOnShift);

            // Preserve any agents that have in-flight tasks; removing them would stall task completion
            // because Phase A completion only iterates over state.Agents.
            var inFlight = new HashSet<AgentId>(_state.AgentTasks.Keys);

            var orderedPool = _agentPool.OrderBy(a => a.Id).ToArray();
            int take = Math.Min(desired, orderedPool.Length);
            var baseSelection = orderedPool.Take(take);

            var activeIds = new HashSet<AgentId>(baseSelection.Select(a => a.Id));
            foreach (var id in inFlight)
            {
                activeIds.Add(id);
            }

            var prevActiveIds = new HashSet<AgentId>(_state.Agents.Select(a => a.Id));

            // Build the new active agent list. Order is irrelevant because stages order by Id, but we
            // keep it stable for deterministic projection.
            var nextAgents = orderedPool.Where(a => activeIds.Contains(a.Id)).ToArray();

            // Spawn newly-active agents near receiving, but OFF the rail (y=2) so they are not
            // mistaken for the train locomotive.
            int receivingX = Math.Min(4, Math.Max(0, _gridWidth - 1));
            int receivingY = Math.Min(2, Math.Max(0, _gridHeight - 1));
            foreach (var agent in nextAgents)
            {
                if (prevActiveIds.Contains(agent.Id))
                {
                    continue; // already active in previous tick
                }

                if (inFlight.Contains(agent.Id))
                {
                    continue; // should not happen, but defensive
                }

                // Offset along the receiving strip to avoid stacks when many workers activate at once.
                int poolIndex = _agentPoolIndexById.TryGetValue(agent.Id, out var idx) ? idx : 0;
                int x = (receivingX + poolIndex) % Math.Max(1, _gridWidth);
                agent.MoveTo(new Cell(x, receivingY));
                agent.ClearPath();
            }

            var prev = _state;
            _state = new TickState(prev.Grid, nextAgents, prev.Ledger, prev.Starships)
            {
                AgentTasks = prev.AgentTasks,
                InboundQueueLotIds = prev.InboundQueueLotIds,
                InTransitLotIds = prev.InTransitLotIds,
                PutAwayTaskLotLinks = prev.PutAwayTaskLotLinks,
                PickTaskLotLinks = prev.PickTaskLotLinks,
                StarshipRuntimes = prev.StarshipRuntimes,
            };
        }
    }

    /// <summary>
    /// Install the live spatial/vessel state the tick pipeline operates over. Called once by the Api
    /// composition root after seeding so the movement + loading stages have agents, a grid, and
    /// starships to drive (task 33.3). Replaces any previously installed state.
    /// </summary>
    public void Install(TickState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _state = state;
        }
    }

    /// <summary>
    /// Build and install a deterministic demo spatial state: an all-aisle <paramref name="gridWidth"/>×
    /// <paramref name="gridHeight"/> grid, <paramref name="agentCount"/> agents spread across it, and
    /// <paramref name="starshipCount"/> starships bound for the given <paramref name="destinations"/>
    /// with an open loading window anchored at <paramref name="now"/>. Everything is derived from
    /// <paramref name="seed"/> so the initial state is reproducible.
    /// </summary>
    /// <param name="seed">The deterministic seed for the synthesized ids/positions.</param>
    /// <param name="now">The current simulated time; loading windows open around it so loading can occur.</param>
    /// <param name="destinations">
    /// Candidate destination colonies for the starships (e.g. the seeded colonies). When empty, no
    /// starships are created (loading stage stays a no-op) but agents/grid are still installed.
    /// </param>
    /// <param name="gridWidth">Grid column count (default 32).</param>
    /// <param name="gridHeight">Grid row count (default 32).</param>
    /// <param name="agentCount">Number of movement agents to synthesize (default 6).</param>
    /// <param name="starshipCount">Number of starships to synthesize (default 3).</param>
    public void Initialize(
        int seed,
        DateTimeOffset now,
        IReadOnlyList<ColonyId> destinations,
        int gridWidth = 32,
        int gridHeight = 32,
        int agentCount = 6,
        int starshipCount = 3,
        int maxAgentCount = 0)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        ArgumentOutOfRangeException.ThrowIfNegative(gridWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(gridHeight);

        _seed = seed;
        _gridWidth = gridWidth;
        _gridHeight = gridHeight;

        // An all-aisle grid keeps every cell traversable so the demo agents can always be routed.
        var grid = new WarehouseGrid(gridWidth, gridHeight);

        int poolCount = maxAgentCount > 0 ? maxAgentCount : agentCount;
        poolCount = Math.Max(0, poolCount);

        _agentPool = BuildAgents(seed, poolCount, gridWidth, gridHeight);

        // Pre-compute a stable pool index for receiving-strip spawn offsets.
        var orderedPool = _agentPool.OrderBy(a => a.Id).ToArray();
        _agentPoolIndexById = orderedPool
            .Select((a, i) => (a.Id, i))
            .ToDictionary(t => t.Id, t => t.i);

        int initialDesired = Math.Min(agentCount, orderedPool.Length);
        var agents = orderedPool.Take(initialDesired).ToArray();

        var starships = BuildStarships(seed, now, destinations, starshipCount);

        Install(new TickState(grid, agents, new ReservationLedger(), starships));
    }

    private static IReadOnlyList<Agent> BuildAgents(int seed, int agentCount, int width, int height)
    {
        var agents = new List<Agent>(Math.Max(0, agentCount));
        if (agentCount <= 0 || width <= 0 || height <= 0)
        {
            return agents;
        }

        for (var i = 0; i < agentCount; i++)
        {
            var agentId = new AgentId(DeterministicIds.Derive(seed, "agent", i));
            var workerId = new WorkerId(DeterministicIds.Derive(seed, "agent-worker", i));

            // Spread the agents along the first row (and wrap to further rows for larger counts) so no
            // two start on the same cell; positions are a pure function of (i, width).
            var start = new Cell(i % width, (i / width) % height);
            agents.Add(new Agent(agentId, workerId, start, cellsPerSecond: 1.5));
        }

        return agents;
    }

    private static IReadOnlyList<Starship> BuildStarships(
        int seed,
        DateTimeOffset now,
        IReadOnlyList<ColonyId> destinations,
        int starshipCount)
    {
        var starships = new List<Starship>(Math.Max(0, starshipCount));
        if (starshipCount <= 0 || destinations.Count == 0)
        {
            return starships;
        }

        for (var i = 0; i < starshipCount; i++)
        {
            var id = new StarshipId(DeterministicIds.Derive(seed, "starship", i));
            var destination = destinations[i % destinations.Count];

            // A wide loading window so FEFO loading admission stays open while berthed.
            // Arrive/depart visuals are driven by StarshipRuntime phases, not window length.
            var window = LoadingWindow.Create(now.AddHours(-1), now.AddHours(48));
            if (window.IsFailure)
            {
                continue; // Defensive: the fixed offsets always yield end > start.
            }

            var starship = Starship.Create(
                id,
                cargoCapacity: VisualSimulationConstants.StarshipCargoCapacityPallets,
                destination,
                new[] { window.Value });
            if (starship.IsSuccess)
            {
                starships.Add(starship.Value);
            }
        }

        return starships;
    }
}
