using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Loading;
using Forge.Application.OperatorParameters;
using Forge.Application.Simulation;
using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;
using Xunit;

namespace Forge.Tests.RuleApplication;

/// <summary>
/// Unit tests for the per-tick <see cref="ApplyTickRulesHandler"/> (task 24.4). They pin down the
/// fixed stage order, the paused/zero-delta no-op, the expiry-decay + LotExpired publication, the
/// deterministic agent-movement stage (reservation hold + unroutable), the starship window-close
/// event, the metrics BacklogChanged emission, and end-to-end reproducibility for identical inputs.
/// Validates: Requirements 1.8, 10.4, 11.1, 12.2, 12.3, 13.2, 14.1, 14.6, 18.4, 18.6, 19.2.
/// </summary>
public sealed class ApplyTickRulesHandlerTests
{
    private static readonly DateTimeOffset Now = new(2350, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delta = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShelfLife = TimeSpan.FromDays(30);

    [Fact]
    public async Task Zero_delta_is_a_deterministic_no_op()
    {
        var ctx = new TestContext();
        // Seed a lot that WOULD expire so we can prove nothing happens on a paused tick.
        ctx.Lots.Seed(ExpiredLot(Now));

        var result = await ctx.Handler.ApplyTickRulesAsync(TimeSpan.Zero);

        Assert.True(result.IsSuccess);
        Assert.Empty(ctx.EventBus.Published);
        Assert.Equal(0, ctx.UnitOfWork.SaveCount);
        Assert.Empty(ctx.Lots.Updated);
    }

    [Fact]
    public async Task Negative_delta_is_a_deterministic_no_op()
    {
        var ctx = new TestContext();
        ctx.Lots.Seed(ExpiredLot(Now));

        var result = await ctx.Handler.ApplyTickRulesAsync(TimeSpan.FromSeconds(-5));

        Assert.True(result.IsSuccess);
        Assert.Empty(ctx.EventBus.Published);
        Assert.Equal(0, ctx.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Stage1_expiry_decay_marks_expired_lots_and_publishes_one_event_each()
    {
        var ctx = new TestContext();
        var expiring = ExpiredLot(Now);            // remaining whole seconds <= 0 at Now
        var fresh = FreshLot(Now, ShelfLife);      // still has shelf-life
        ctx.Lots.Seed(expiring);
        ctx.Lots.Seed(fresh);

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        Assert.True(expiring.IsExpired);
        Assert.False(fresh.IsExpired);

        var expired = ctx.EventBus.Published.OfType<LotExpired>().ToArray();
        var single = Assert.Single(expired);
        Assert.Equal(expiring.Id, single.LotId);
        Assert.Equal(Now, single.At);

        // Only the transitioned lot is staged for update, and the tick committed once.
        Assert.Contains(expiring, ctx.Lots.Updated);
        Assert.DoesNotContain(fresh, ctx.Lots.Updated);
        Assert.Equal(1, ctx.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Already_expired_lot_is_idempotent_no_event_no_commit()
    {
        var ctx = new TestContext();
        var lot = ExpiredLot(Now);
        // Expire it up front so this tick finds it already expired.
        Assert.True(lot.TryExpireAt(Now, out _));
        ctx.Lots.Seed(lot);

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        Assert.Empty(ctx.EventBus.Published.OfType<LotExpired>());
        Assert.Equal(0, ctx.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Stage5_metrics_emits_BacklogChanged_for_outbound_demand_received_this_tick()
    {
        var ctx = new TestContext();
        // Order whose delivery window opens within (Now-Delta, Now].
        ctx.Orders.Seed(OrderOpeningAt(Now, quantity: 7));

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        var backlog = ctx.EventBus.Published.OfType<BacklogChanged>()
            .Single(e => e.Kind == BacklogKind.Outbound.ToString());
        Assert.Equal(7, backlog.NewSize);
        Assert.Equal(7, ctx.Metrics.Outbound);
    }

    [Fact]
    public async Task Stage5_metrics_emits_receiving_backlog_from_unassigned_putaway_tasks()
    {
        var ctx = new TestContext();
        ctx.Tasks.SeedUnassigned(MakeTask(WarehouseTaskType.PutAway));
        ctx.Tasks.SeedUnassigned(MakeTask(WarehouseTaskType.PutAway));
        ctx.Tasks.SeedUnassigned(MakeTask(WarehouseTaskType.Pick)); // not counted

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        var receiving = ctx.EventBus.Published.OfType<BacklogChanged>()
            .Single(e => e.Kind == BacklogKind.Receiving.ToString());
        Assert.Equal(2, receiving.NewSize);
    }

    [Fact]
    public async Task Stage3_agent_movement_advances_agent_by_speed_times_delta()
    {
        var ctx = new TestContext();
        var grid = new WarehouseGrid(10, 1); // all-aisle single row
        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 1);
        agent.AssignPath(new Forge.Domain.Spatial.Path(new[]
        {
            new Cell(0, 0), new Cell(1, 0), new Cell(2, 0), new Cell(3, 0),
            new Cell(4, 0), new Cell(5, 0),
        }));
        ctx.TickState.Set(new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>()));

        // 1 cell/s over 10s can reach up to 5 cells (the whole path).
        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Cell(5, 0), agent.Position);
        Assert.Equal(1, ctx.UnitOfWork.SaveCount); // agent mutation staged + committed
    }

    [Fact]
    public async Task Stage3_idle_agent_is_dispatched_a_destination_and_moves()
    {
        // The dispatch step: an agent with NO assigned path is handed a patrol destination, routed
        // there, and advanced this tick — so the warehouse stays visibly in motion (the fix that made
        // the agents actually travel). Verifies the agent leaves its start cell and its position stays
        // on the (all-aisle) grid.
        var ctx = new TestContext();
        var grid = new WarehouseGrid(16, 16); // all-aisle: every anchor is reachable
        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 2);
        // NOTE: no AssignPath — the agent is idle and must be dispatched by the stage itself.
        ctx.TickState.Set(new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>()));

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        // It was dispatched a path and advanced off its start cell.
        Assert.NotEqual(new Cell(0, 0), agent.Position);
        Assert.InRange(agent.Position.X, 0, grid.Width - 1);
        Assert.InRange(agent.Position.Y, 0, grid.Height - 1);
        Assert.NotNull(agent.CurrentPath);
        Assert.Equal(1, ctx.UnitOfWork.SaveCount); // the agent move was staged + committed
    }

    [Fact]
    public async Task Stage3_dispatch_is_deterministic_for_identical_state()
    {
        // Same agent id + same start cell + same delta must yield the same dispatched position, so the
        // "living warehouse" motion stays reproducible (Req 19.6).
        var agentId = new AgentId(new Guid("33333333-3333-3333-3333-333333333333"));

        static async Task<Cell> Run(AgentId id)
        {
            var ctx = new TestContext();
            var grid = new WarehouseGrid(16, 16);
            var agent = new Agent(id, WorkerId.New(), new Cell(2, 2), cellsPerSecond: 2);
            ctx.TickState.Set(new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>()));
            await ctx.Handler.ApplyTickRulesAsync(Delta);
            return agent.Position;
        }

        var first = await Run(agentId);
        var second = await Run(agentId);

        Assert.Equal(first, second);
        Assert.NotEqual(new Cell(2, 2), first); // it actually moved
    }

    [Fact]
    public async Task Stage3_lower_id_agent_wins_contested_segment_and_higher_id_holds()
    {
        var ctx = new TestContext();
        var grid = new WarehouseGrid(5, 1);

        var lowId = new Guid("11111111-1111-1111-1111-111111111111");
        var highId = new Guid("22222222-2222-2222-2222-222222222222");
        var low = new Agent(new AgentId(lowId), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 1);
        var high = new Agent(new AgentId(highId), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 1);

        // Both want to traverse the same segment (0,0)->(1,0) starting at Now.
        var sharedPath = new Forge.Domain.Spatial.Path(new[] { new Cell(0, 0), new Cell(1, 0) });
        low.AssignPath(sharedPath);
        high.AssignPath(sharedPath);

        ctx.TickState.Set(new TickState(grid, new[] { high, low }, new ReservationLedger(), Array.Empty<Starship>()));

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        // Lower-id agent moved; higher-id agent was held in place (Req 19.2, 19.6).
        Assert.Equal(new Cell(1, 0), low.Position);
        Assert.Equal(new Cell(0, 0), high.Position);
    }

    [Fact]
    public async Task Stage4_starship_window_close_raises_LoadingWindowClosed()
    {
        var ctx = new TestContext();
        // Window that was open at Now-Delta but is closed at Now.
        var window = LoadingWindow.Create(Now.AddMinutes(-10), Now.AddSeconds(-5)).Value;
        var starship = Starship.Create(StarshipId.New(), cargoCapacity: 100, ColonyId.New(), new[] { window }).Value;

        var grid = new WarehouseGrid(2, 1);
        ctx.TickState.Set(new TickState(grid, Array.Empty<Agent>(), new ReservationLedger(), new[] { starship }));

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        var closed = Assert.Single(ctx.EventBus.Published.OfType<LoadingWindowClosed>());
        Assert.Equal(starship.Id, closed.StarshipId);
        Assert.Equal(Now, closed.At);
    }

    [Fact]
    public async Task No_tick_state_leaves_movement_and_loading_stages_as_no_ops()
    {
        var ctx = new TestContext(); // provider returns null by default
        ctx.Lots.Seed(FreshLot(Now, ShelfLife));

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        Assert.Empty(ctx.EventBus.Published.OfType<UnroutableTask>());
        Assert.Empty(ctx.EventBus.Published.OfType<LoadingWindowClosed>());
    }

    [Fact]
    public async Task Identical_inputs_produce_identical_outcomes()
    {
        // Reproducibility: two independent runs from the same seeded state publish the same events
        // in the same order and mutate identically (Req 19.6).
        var a = RunScenario();
        var b = RunScenario();

        var eventsA = await a;
        var eventsB = await b;

        Assert.Equal(eventsA, eventsB);
    }

    // Fixed ids so two independent runs from identical seeded state are byte-for-byte comparable
    // (the point is reproducibility of the RULES, not of the id generator).
    private static readonly GelLotId FixedLotId =
        new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static async Task<IReadOnlyList<string>> RunScenario()
    {
        var ctx = new TestContext();
        ctx.Lots.Seed(GelLot.Create(FixedLotId, GelTypeId.New(),
            new Formulation(new TemperatureRange(-10m, 5m), ShelfLife, new[] { "vanilla" }),
            producedAt: Now - ShelfLife, quantity: 10));
        ctx.Orders.Seed(OrderOpeningAt(Now, quantity: 3));
        ctx.Tasks.SeedUnassigned(MakeTask(WarehouseTaskType.PutAway));

        await ctx.Handler.ApplyTickRulesAsync(Delta);

        // Represent each published event canonically so two runs can be compared for equality.
        return ctx.EventBus.Published.Select(e => e switch
        {
            LotExpired le => $"LotExpired:{le.LotId}",
            BacklogChanged bc => $"Backlog:{bc.Kind}:{bc.NewSize}",
            _ => e.GetType().Name,
        }).ToArray();
    }

    // ---- Builders ----

    private static GelLot ExpiredLot(DateTimeOffset now) =>
        // ProducedAt so that ExpiresAt == now (remaining whole seconds == 0 -> expires).
        GelLot.Create(GelLotId.New(), GelTypeId.New(),
            new Formulation(new TemperatureRange(-10m, 5m), ShelfLife, new[] { "vanilla" }),
            producedAt: now - ShelfLife, quantity: 10);

    private static GelLot FreshLot(DateTimeOffset now, TimeSpan shelfLife) =>
        GelLot.Create(GelLotId.New(), GelTypeId.New(),
            new Formulation(new TemperatureRange(-10m, 5m), shelfLife, new[] { "vanilla" }),
            producedAt: now, quantity: 10);

    private static ColonyOrder OrderOpeningAt(DateTimeOffset now, int quantity) =>
        new(ColonyOrderId.New(), ColonyId.New(),
            new[] { new OrderLine(GelTypeId.New(), quantity) },
            now,                // delivery window opens exactly at now -> within (now-delta, now]
            now.AddHours(1));

    private static WarehouseTask MakeTask(WarehouseTaskType type) =>
        WarehouseTask.Create(WarehouseTaskId.New(), type, new Cell(0, 0), new Cell(1, 0), TimeSpan.Zero).Value;

    [Fact]
    public async Task TaskExecution_assigns_unassigned_putaway_task_to_idle_agent()
    {
        var ctx = new TestContext();
        var grid = new WarehouseGrid(8, 1); // all-aisle single row so (0,0)->(1,0) is routable
        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 2);
        ctx.TickState.Set(new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>()));

        // A put-away task at destination (1,0), initially unassigned.
        var task = MakeTask(WarehouseTaskType.PutAway);
        ctx.Tasks.SeedUnassigned(task);

        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        // The idle agent claimed the task: it is now assigned to that agent's worker and given a path.
        Assert.Equal(agent.Worker, task.AssignedWorker);
        Assert.NotNull(agent.CurrentPath);
        // Backlog dropped to zero because the task is no longer unassigned.
        Assert.Equal(0, ctx.Metrics.Receiving);
    }

    [Fact]
    public async Task TaskExecution_completes_task_when_agent_reaches_destination_and_drains_backlog()
    {
        var ctx = new TestContext();
        var grid = new WarehouseGrid(8, 1);
        // Fast agent so it clears the single (0,0)->(1,0) step within one tick.
        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 5);
        ctx.TickState.Set(new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>()));

        var task = MakeTask(WarehouseTaskType.PutAway); // destination (1,0)
        ctx.Tasks.SeedUnassigned(task);

        // Tick 1: assign + start moving toward the destination.
        await ctx.Handler.ApplyTickRulesAsync(Delta);
        // Tick 2: the agent has arrived at (1,0); the task completes.
        var result = await ctx.Handler.ApplyTickRulesAsync(Delta);

        Assert.True(result.IsSuccess);
        Assert.Equal(new Cell(1, 0), agent.Position);
        Assert.Equal(Forge.Domain.Tasks.TaskStatus.Completed, task.Status);
        Assert.Contains(ctx.EventBus.Published, e => e is TaskCompleted tc && tc.TaskId.Equals(task.Id));
        // Inbound throughput registered the completed put-away lot.
        Assert.True(ctx.Metrics.InboundThroughput > 0d);
        Assert.Equal(0, ctx.Metrics.Receiving);
    }

    [Fact]
    public async Task TaskBound_agent_is_not_hijacked_by_patrol_dispatch_and_completes_over_several_ticks()
    {
        // Regression: a task-bound agent used to be treated as "idle" by the movement stage the moment
        // it reached its work cell, get dispatched a NEW patrol path, wander off, and therefore never
        // satisfy the arrival check — so the task never completed and the backlog never drained (agents
        // frozen "all over the map"). This drives multiple ticks and asserts the task actually completes
        // and the agent is released (no lingering link, no patrol path).
        var ctx = new TestContext();
        var grid = new WarehouseGrid(16, 16);
        // Slow agent so arrival takes more than one tick — exercising the multi-tick travel + the
        // "hold on arrival until completion" behaviour rather than a same-tick complete.
        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(0, 0), cellsPerSecond: 0.5);
        var state = new TickState(grid, new[] { agent }, new ReservationLedger(), Array.Empty<Starship>());
        ctx.TickState.Set(state);

        var task = MakeTask(WarehouseTaskType.PutAway); // destination (1,0), in-bounds/non-degenerate
        ctx.Tasks.SeedUnassigned(task);

        // Run enough ticks for the slow agent to travel to the work cell and complete.
        for (var i = 0; i < 8; i++)
        {
            await ctx.Handler.ApplyTickRulesAsync(Delta);
        }

        Assert.Equal(Forge.Domain.Tasks.TaskStatus.Completed, task.Status);
        Assert.Contains(ctx.EventBus.Published, e => e is TaskCompleted tc && tc.TaskId.Equals(task.Id));
        // The agent was released: no lingering task link.
        Assert.False(state.AgentTasks.ContainsKey(agent.Id));
        Assert.Equal(0, ctx.Metrics.Receiving);
    }

    // ---- Test harness ----

    private sealed class TestContext
    {
        public FakeLotRepository Lots { get; } = new();
        public FakeZoneRepository Zones { get; } = new();
        public FakeOrderRepository Orders { get; } = new();
        public FakeTaskRepository Tasks { get; } = new();
        public FakeTickStateProvider TickState { get; } = new();
        public FakeEventBus EventBus { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public WarehouseMetrics Metrics { get; } = new();
        public OperatorParameterState OperatorParameters { get; } =
            new(new OperatorParameterOptions { WorkerMax = 25, ModeledDockBays = 4 });
        public ApplyTickRulesHandler Handler { get; }

        public TestContext()
        {
            Handler = new ApplyTickRulesHandler(
                new FixedClock(Now),
                Lots,
                Zones,
                Orders,
                Tasks,
                new PathPlannerAdapter(),
                new StarshipLoadingService(),
                Metrics,
                OperatorParameters,
                TickState,
                UnitOfWork,
                EventBus);
        }
    }

    private sealed class PathPlannerAdapter : IPathPlanner
    {
        private readonly AStarPathPlanner _planner = new();
        public PathResult Plan(WarehouseGrid grid, Cell origin, Cell destination) =>
            _planner.Plan(grid, origin, destination);
    }

    private sealed class FakeTickStateProvider : ITickStateProvider
    {
        private TickState? _state;
        public void Set(TickState state) => _state = state;
        public Task<TickState?> GetTickStateAsync(CancellationToken ct = default) => Task.FromResult(_state);

        public void EnqueueInboundPutAway(GelLotId lotId, WarehouseTaskId putAwayTaskId) { }

        public void ApplyWorkerCount(int workersOnShift) { }
    }

    private sealed class FakeLotRepository : IGelLotRepository
    {
        private readonly List<GelLot> _lots = new();
        public List<GelLot> Updated { get; } = new();

        public void Seed(GelLot lot) => _lots.Add(lot);

        public Task<GelLot?> GetByIdAsync(GelLotId id, CancellationToken ct = default) =>
            Task.FromResult(_lots.FirstOrDefault(l => l.Id.Equals(id)));

        public Task<IReadOnlyList<GelLot>> GetByGelTypeAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(_lots.Where(l => l.GelTypeId.Equals(gelTypeId)).ToArray());

        public Task<IReadOnlyList<GelLot>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(_lots.ToArray());

        public void Add(GelLot lot) => _lots.Add(lot);
        public void Update(GelLot lot) => Updated.Add(lot);
    }

    private sealed class FakeZoneRepository : IZoneRepository
    {
        private readonly List<TemperatureZone> _zones = new();

        public void Seed(TemperatureZone zone) => _zones.Add(zone);

        public Task<TemperatureZone?> GetByIdAsync(ZoneId id, CancellationToken ct = default) =>
            Task.FromResult(_zones.FirstOrDefault(z => z.Id.Equals(id)));

        public Task<IReadOnlyList<TemperatureZone>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemperatureZone>>(_zones.ToArray());

        public void Add(TemperatureZone zone) => _zones.Add(zone);
        public void Update(TemperatureZone zone) { }
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly List<ColonyOrder> _orders = new();
        public void Seed(ColonyOrder order) => _orders.Add(order);

        public Task<ColonyOrder?> GetByIdAsync(ColonyOrderId id, CancellationToken ct = default) =>
            Task.FromResult(_orders.FirstOrDefault(o => o.Id.Equals(id)));

        public Task<IReadOnlyList<ColonyOrder>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ColonyOrder>>(_orders.ToArray());

        public Task<IReadOnlyList<ColonyOrder>> GetByColonyAsync(ColonyId colonyId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ColonyOrder>>(_orders.Where(o => o.Colony.Equals(colonyId)).ToArray());

        public void Add(ColonyOrder order) => _orders.Add(order);
        public void Update(ColonyOrder order) { }
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        private readonly List<WarehouseTask> _all = new();

        public void SeedUnassigned(WarehouseTask task) => _all.Add(task);

        public Task<WarehouseTask?> GetByIdAsync(WarehouseTaskId id, CancellationToken ct = default) =>
            Task.FromResult(_all.FirstOrDefault(t => t.Id.Equals(id)));

        public Task<IReadOnlyList<WarehouseTask>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(_all.ToArray());

        // A task is "unassigned" while it has no assigned worker and is not yet completed. Because the
        // task-execution stage mutates the SAME task instances held here, this reflects assignment/
        // completion immediately — exactly like the live EF-backed repository over a shared context.
        public Task<IReadOnlyList<WarehouseTask>> GetUnassignedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(
                _all.Where(t => t.AssignedWorker is null
                                && t.Status is not Forge.Domain.Tasks.TaskStatus.Completed).ToArray());

        public void Add(WarehouseTask task) => _all.Add(task);
        public void Update(WarehouseTask task) { }
    }

    private sealed class FakeEventBus : IEventBus
    {
        public List<IDomainEvent> Published { get; } = new();
        public bool IsAvailable => true;

        public Task PublishAsync(IDomainEvent @event, CancellationToken ct = default)
        {
            Published.Add(@event);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IDomainEvent => new Noop();

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; }
        public ClockMode Mode => ClockMode.Accelerated;
        public double AccelerationFactor => 1;
        public void Configure(ClockMode mode, double accelerationFactor) { }
        public void Pause() { }
        public void Resume() { }
        public TimeSpan Advance(TimeSpan wallDelta) => TimeSpan.Zero;
    }
}
