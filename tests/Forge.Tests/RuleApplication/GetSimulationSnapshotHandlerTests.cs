using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Docks;
using Forge.Application.OperatorParameters;
using Forge.Application.Queries;
using Forge.Application.Simulation;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Vessels;
using Xunit;

namespace Forge.Tests.RuleApplication;

/// <summary>
/// Unit tests for the read-only <see cref="GetSimulationSnapshotHandler"/> (task 24.8). They prove the
/// snapshot faithfully reflects the current inventory / starship / agent / zone / metrics / parameter
/// state, and — critically — that building a snapshot mutates <b>nothing</b>: no repository writes, no
/// domain-aggregate changes, and repeated calls return identical projections.
/// Validates: Requirements 9.3, 23.3.
/// </summary>
public sealed class GetSimulationSnapshotHandlerTests
{
    private static readonly DateTimeOffset Now = new(2350, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ShelfLife = TimeSpan.FromDays(30);

    [Fact]
    public async Task Snapshot_reflects_current_zone_lot_agent_and_starship_state()
    {
        var ctx = new TestContext();

        var zone = ctx.SeedZone(minC: -10m, maxC: 5m, capacity: 100, stored: 40);
        var lot = ctx.SeedLot(zone.Id, quantity: 12);

        var agent = new Agent(AgentId.New(), WorkerId.New(), new Cell(2, 3), cellsPerSecond: 1.5);
        agent.AssignPath(new Forge.Domain.Spatial.Path(new[] { new Cell(2, 3), new Cell(3, 3) }));
        var window = LoadingWindow.Create(Now, Now.AddHours(1)).Value;
        var starship = Starship.Create(StarshipId.New(), cargoCapacity: 500, ColonyId.New(), new[] { window }, loadedQuantity: 120).Value;
        ctx.SetTickState(new[] { agent }, new[] { starship });

        var snapshot = await ctx.Handler.HandleAsync();

        // Zones.
        var zoneDto = Assert.Single(snapshot.Zones);
        Assert.Equal(zone.Id.Value, zoneDto.Id);
        Assert.Equal(-10m, zoneDto.MinC);
        Assert.Equal(5m, zoneDto.MaxC);
        Assert.Equal(100, zoneDto.Capacity);
        Assert.Equal(40, zoneDto.Stored);

        // Lots.
        var lotDto = Assert.Single(snapshot.Lots);
        Assert.Equal(lot.Id.Value, lotDto.Id);
        Assert.Equal(lot.GelTypeId.Value, lotDto.GelTypeId);
        Assert.Equal(lot.ExpiresAt, lotDto.ExpiresAt);
        Assert.Equal(12, lotDto.Quantity);
        Assert.False(lotDto.IsExpired);
        Assert.False(lotDto.AtRisk);
        Assert.Equal(zone.Id.Value, lotDto.ZoneId);

        // Agents.
        var agentDto = Assert.Single(snapshot.Agents);
        Assert.Equal(agent.Id.Value, agentDto.Id);
        Assert.Equal(2, agentDto.X);
        Assert.Equal(3, agentDto.Y);
        Assert.Equal(1.5, agentDto.CellsPerSecond);
        Assert.Equal("Active", agentDto.Phase);
        Assert.Null(agentDto.CarryingLotId);
        Assert.Equal(2, agentDto.PathCells.Count);
        Assert.Equal(3, agentDto.PathCells[1].X);

        // Starships.
        var shipDto = Assert.Single(snapshot.Starships);
        Assert.Equal(starship.Id.Value, shipDto.Id);
        Assert.Equal(500, shipDto.Capacity);
        Assert.Equal(120, shipDto.Loaded);
        Assert.Equal(starship.Destination.Value, shipDto.DestinationColony);
        Assert.Equal("Loading", shipDto.Phase);
        Assert.Equal(0, shipDto.DockIndex);
        Assert.Equal(0, shipDto.DockIndex);
        Assert.Equal(window.Start, Assert.Single(shipDto.Windows).Start);
    }

    [Fact]
    public async Task Snapshot_reflects_current_metrics_and_operator_parameters()
    {
        var ctx = new TestContext();
        // Drive the live metrics + parameter state, then confirm the snapshot mirrors them.
        ctx.Metrics.SetReceiving(6, Now);
        ctx.Metrics.SetOutbound(9, Now);
        ctx.Metrics.RecordInboundProcessed(lots: 4, simulatedSeconds: 2);

        var snapshot = await ctx.Handler.HandleAsync();

        Assert.Equal(6, snapshot.Metrics.Receiving);
        Assert.Equal(9, snapshot.Metrics.Outbound);
        Assert.Equal(2.0, snapshot.Metrics.InboundThroughput); // 4 lots / 2 s

        // Parameters mirror the configured initial operator-parameter state.
        Assert.Equal(OperatorParameterState.DefaultSimSpeed, snapshot.Parameters.SimSpeed);
        Assert.Equal(3, snapshot.Parameters.WorkersOnShift);   // WorkerMax default
        Assert.Equal(2, snapshot.Parameters.OpenDockBays);     // ModeledDockBays default
        Assert.Equal(SlottingStrategyKey.VelocityAffinity, snapshot.Parameters.SlottingStrategy);
    }

    [Fact]
    public async Task Null_tick_state_yields_empty_agents_and_starships_without_failing()
    {
        var ctx = new TestContext(); // provider returns null by default
        ctx.SeedZone(minC: 0m, maxC: 4m, capacity: 10, stored: 0);

        var snapshot = await ctx.Handler.HandleAsync();

        Assert.Empty(snapshot.Agents);
        Assert.Empty(snapshot.Starships);
        Assert.Single(snapshot.Zones); // repository-backed state still projected
    }

    [Fact]
    public async Task Handling_the_query_does_not_mutate_any_state()
    {
        var ctx = new TestContext();
        var zone = ctx.SeedZone(minC: -10m, maxC: 5m, capacity: 100, stored: 40);
        var lot = ctx.SeedLot(zone.Id, quantity: 12);
        var starship = Starship.Create(
            StarshipId.New(), cargoCapacity: 500, ColonyId.New(),
            new[] { LoadingWindow.Create(Now, Now.AddHours(1)).Value }, loadedQuantity: 120).Value;
        ctx.SetTickState(Array.Empty<Agent>(), new[] { starship });

        // Capture observable state before.
        var zoneStoredBefore = zone.StoredQuantity;
        var lotQtyBefore = lot.Quantity;
        var lotExpiredBefore = lot.IsExpired;
        var shipLoadedBefore = starship.LoadedQuantity;
        var receivingBefore = ctx.Metrics.Receiving;
        var outboundBefore = ctx.Metrics.Outbound;

        // Two consecutive queries.
        var first = await ctx.Handler.HandleAsync();
        var second = await ctx.Handler.HandleAsync();

        // No repository writes occurred (only reads).
        Assert.Empty(ctx.Zones.Written);
        Assert.Empty(ctx.Lots.Written);

        // Domain aggregates are untouched.
        Assert.Equal(zoneStoredBefore, zone.StoredQuantity);
        Assert.Equal(lotQtyBefore, lot.Quantity);
        Assert.Equal(lotExpiredBefore, lot.IsExpired);
        Assert.Equal(shipLoadedBefore, starship.LoadedQuantity);
        Assert.Equal(receivingBefore, ctx.Metrics.Receiving);
        Assert.Equal(outboundBefore, ctx.Metrics.Outbound);

        // Repeated queries are identical projections (read-only + deterministic). The snapshot DTO
        // carries collection members, whose default record equality is by-reference, so compare the
        // projected element values structurally rather than relying on SimulationSnapshotDto.Equals.
        Assert.Equal(first.Zones, second.Zones); // TemperatureZoneDto is all-scalar -> structural equality.
        Assert.Equal(first.Lots, second.Lots);   // GelLotDto is all-scalar -> structural equality.
        Assert.Equal(
            first.Agents.Select(a => (a.Id, a.X, a.Y, a.CellsPerSecond, a.Phase, a.CarryingLotId)),
            second.Agents.Select(a => (a.Id, a.X, a.Y, a.CellsPerSecond, a.Phase, a.CarryingLotId)));
        Assert.Equal(
            first.Starships.Select(s => (s.Id, s.Capacity, s.Loaded, s.DestinationColony, s.Phase, s.DockIndex)),
            second.Starships.Select(s => (s.Id, s.Capacity, s.Loaded, s.DestinationColony, s.Phase, s.DockIndex)));
        Assert.Equal(first.Metrics, second.Metrics);
        Assert.Equal(first.Parameters, second.Parameters);
    }

    // ---- Test harness ----

    private sealed class TestContext
    {
        private sealed class FakeClock : IClock
        {
            public FakeClock(DateTimeOffset now) => Now = now;

            public DateTimeOffset Now { get; private set; }

            public ClockMode Mode => ClockMode.RealTime;
            public double AccelerationFactor => 1.0;

            public void Configure(ClockMode mode, double accelerationFactor) { }
            public void Pause() { }
            public void Resume() { }

            public TimeSpan Advance(TimeSpan wallDelta) => wallDelta;
        }

        public FakeZoneRepository Zones { get; } = new();
        public FakeLotRepository Lots { get; } = new();
        public FakeTickStateProvider TickState { get; } = new();
        public WarehouseMetrics Metrics { get; } = new();
        public DockScheduler DockScheduler { get; } = new();
        public DockBayId PrimaryBay { get; } = DockBayId.New();
        public IClock Clock { get; } = new FakeClock(Now);
        public OperatorParameterState Parameters { get; } =
            new(new OperatorParameterOptions { WorkerMax = 3, ModeledDockBays = 2 });

        public GetSimulationSnapshotHandler Handler { get; }

        public TestContext()
        {
            Handler = new GetSimulationSnapshotHandler(
                Zones, Lots, TickState, Clock, Metrics, DockScheduler, PrimaryBay, Parameters);
        }

        public TemperatureZone SeedZone(decimal minC, decimal maxC, int capacity, int stored)
        {
            var zone = TemperatureZone.Create(
                ZoneId.New(), new TemperatureRange(minC, maxC), capacity, stored).Value;
            Zones.Seed(zone);
            return zone;
        }

        public GelLot SeedLot(ZoneId zoneId, int quantity)
        {
            var lot = GelLot.Create(
                GelLotId.New(),
                GelTypeId.New(),
                new Formulation(new TemperatureRange(-10m, 5m), ShelfLife, new[] { "vanilla" }),
                producedAt: Now,
                quantity: quantity,
                assignedZoneId: zoneId);
            Lots.Seed(lot);
            return lot;
        }

        public void SetTickState(IReadOnlyList<Agent> agents, IReadOnlyList<Starship> starships)
        {
            var state = new TickState(new WarehouseGrid(10, 10), agents, new ReservationLedger(), starships);
            var i = 0;
            foreach (var ship in starships.OrderBy(s => s.Id))
            {
                state.StarshipRuntimes[ship.Id] = new StarshipRuntime
                {
                    Phase = StarshipPhases.Loading,
                    PhaseEnteredAt = Now,
                    DockIndex = i++,
                    UnloadRemaining = 0,
                };
            }

            TickState.Set(state);
        }
    }

    private sealed class FakeZoneRepository : IZoneRepository
    {
        private readonly List<TemperatureZone> _zones = new();
        public List<TemperatureZone> Written { get; } = new();

        public void Seed(TemperatureZone zone) => _zones.Add(zone);

        public Task<TemperatureZone?> GetByIdAsync(ZoneId id, CancellationToken ct = default) =>
            Task.FromResult(_zones.FirstOrDefault(z => z.Id.Equals(id)));

        public Task<IReadOnlyList<TemperatureZone>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemperatureZone>>(_zones.ToArray());

        public void Add(TemperatureZone zone) => Written.Add(zone);
        public void Update(TemperatureZone zone) => Written.Add(zone);
    }

    private sealed class FakeLotRepository : IGelLotRepository
    {
        private readonly List<GelLot> _lots = new();
        public List<GelLot> Written { get; } = new();

        public void Seed(GelLot lot) => _lots.Add(lot);

        public Task<GelLot?> GetByIdAsync(GelLotId id, CancellationToken ct = default) =>
            Task.FromResult(_lots.FirstOrDefault(l => l.Id.Equals(id)));

        public Task<IReadOnlyList<GelLot>> GetByGelTypeAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(_lots.Where(l => l.GelTypeId.Equals(gelTypeId)).ToArray());

        public Task<IReadOnlyList<GelLot>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(_lots.ToArray());

        public void Add(GelLot lot) => Written.Add(lot);
        public void Update(GelLot lot) => Written.Add(lot);
    }

    private sealed class FakeTickStateProvider : ITickStateProvider
    {
        private TickState? _state;
        public void Set(TickState state) => _state = state;
        public Task<TickState?> GetTickStateAsync(CancellationToken ct = default) => Task.FromResult(_state);

        public void ApplyWorkerCount(int workersOnShift) { }

        public void EnqueueInboundPutAway(GelLotId lotId, WarehouseTaskId putAwayTaskId) { }
    }
}
