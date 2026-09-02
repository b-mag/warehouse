using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Docks;
using Forge.Application.Inbound;
using Forge.Application.Simulation;
using Forge.Application.Slotting;

using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Events;
using Forge.Domain.Gels;
using Forge.Domain.Tasks;

using Xunit;

namespace Forge.Tests.RuleApplication;

// Feature: nutrient-forge, Task 24.2 — RecordInboundGelReceiptHandler + PutAway via slotting.
//
// Covers Requirements 11.2 (PutAway via slotting), 11.3 (blocked placement when unslottable),
// 11.4 (expiry derived from formulation nominal shelf-life), 14.1 (receive at a dock bay then
// PutAway), and 14.6 (no dock slot -> queue + receiving backlog + DockBlocked/BlockedArrival).
public sealed class RecordInboundGelReceiptHandlerTests
{
    private static readonly DateTimeOffset Now = new(2350, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ShelfLife = TimeSpan.FromDays(30);

    [Fact]
    public async Task Receipt_slots_lot_and_generates_a_committed_PutAway_task()
    {
        var gelType = MakeGelType(velocity: 1.0, storage: new TemperatureRange(0m, 4m));
        var zone = MakeZone(new TemperatureRange(-1m, 5m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { zone }, openBay: true);

        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(gelType.Id, Now.AddDays(-1), 5, fixture.BayId));

        Assert.True(result.IsSuccess);

        // A single PutAway task was generated and the lot was staged, then committed once (Req 11.2, 14.1).
        var task = Assert.Single(fixture.Tasks.Added);
        Assert.Equal(WarehouseTaskType.PutAway, task.Type);
        Assert.Single(fixture.Lots.Added);
        Assert.Equal(1, fixture.UnitOfWork.SaveCount);

        // No blocked events on the happy path.
        Assert.Empty(fixture.Bus.Events);
        Assert.Equal(0, fixture.Metrics.Receiving);
    }

    [Fact]
    public async Task Lot_expiry_is_derived_from_formulation_nominal_shelf_life()
    {
        // Req 11.4: ExpiresAt = ProducedAt + NominalShelfLife, computed by GelLot.Create.
        var gelType = MakeGelType(velocity: 0.0, storage: new TemperatureRange(0m, 4m));
        var zone = MakeZone(new TemperatureRange(-5m, 10m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { zone }, openBay: true);

        var producedAt = Now.AddDays(-2);
        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(gelType.Id, producedAt, 3, fixture.BayId));

        Assert.True(result.IsSuccess);
        var lot = Assert.Single(fixture.Lots.Added);
        Assert.Equal(producedAt, lot.ProducedAt);
        Assert.Equal(producedAt + ShelfLife, lot.ExpiresAt);
    }

    [Fact]
    public async Task Unslottable_raises_BlockedPlacement_and_creates_no_task()
    {
        // Req 11.3 / 16.3: no compatible zone with capacity -> BlockedPlacement, no infeasible task,
        // inventory consistent (nothing committed).
        var gelType = MakeGelType(velocity: 1.0, storage: new TemperatureRange(0m, 4m));
        // Incompatible zone (its range does not contain the gel's storage range).
        var incompatible = MakeZone(new TemperatureRange(10m, 20m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { incompatible }, openBay: true);

        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(gelType.Id, Now.AddDays(-1), 5, fixture.BayId));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Unslottable, result.Error.Kind);
        Assert.Empty(fixture.Tasks.Added);
        Assert.Empty(fixture.Lots.Added);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);

        var blocked = Assert.Single(fixture.Bus.Events);
        Assert.IsType<BlockedPlacement>(blocked);
    }

    [Fact]
    public async Task No_dock_slot_queues_arrival_increments_backlog_and_raises_blocked_events()
    {
        // Req 14.6: a closed bay yields no dock slot -> the arrival is queued, the receiving backlog
        // is incremented, and DockBlocked + BlockedArrival are raised. No task, nothing committed.
        var gelType = MakeGelType(velocity: 1.0, storage: new TemperatureRange(0m, 4m));
        var zone = MakeZone(new TemperatureRange(-1m, 5m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { zone }, openBay: false);

        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(gelType.Id, Now.AddDays(-1), 5, fixture.BayId));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.SlotUnavailable, result.Error.Kind);

        Assert.Empty(fixture.Tasks.Added);
        Assert.Empty(fixture.Lots.Added);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);

        // Receiving backlog reflects the queued arrival (Req 14.6, 14.7).
        Assert.Equal(1, fixture.Metrics.Receiving);

        // DockBlocked + BlockedArrival + the BacklogChanged event.
        Assert.Contains(fixture.Bus.Events, e => e is DockBlocked);
        Assert.Contains(fixture.Bus.Events, e => e is BlockedArrival);
        Assert.Contains(fixture.Bus.Events, e => e is BacklogChanged);
    }

    [Fact]
    public async Task Unknown_gel_type_is_rejected_leaving_inventory_unchanged()
    {
        var gelType = MakeGelType(velocity: 1.0, storage: new TemperatureRange(0m, 4m));
        var zone = MakeZone(new TemperatureRange(-1m, 5m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { zone }, openBay: true);

        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(GelTypeId.New(), Now.AddDays(-1), 5, fixture.BayId));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
        Assert.Empty(fixture.Lots.Added);
        Assert.Empty(fixture.Tasks.Added);
        Assert.Equal(0, fixture.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Non_positive_quantity_is_rejected()
    {
        var gelType = MakeGelType(velocity: 1.0, storage: new TemperatureRange(0m, 4m));
        var zone = MakeZone(new TemperatureRange(-1m, 5m), capacity: 10);
        var fixture = new Fixture(gelType, zones: new[] { zone }, openBay: true);

        var result = await fixture.Handler.HandleAsync(
            new RecordInboundGelReceiptCommand(gelType.Id, Now.AddDays(-1), 0, fixture.BayId));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
        Assert.Empty(fixture.Lots.Added);
    }

    // ---- Test helpers ----

    private static GelType MakeGelType(double velocity, TemperatureRange storage) =>
        new(GelTypeId.New(), new Formulation(storage, ShelfLife, new[] { "vanilla" }), velocity);

    private static TemperatureZone MakeZone(TemperatureRange range, int capacity) =>
        TemperatureZone.Create(ZoneId.New(), range, capacity).Value;

    private sealed class Fixture
    {
        public Fixture(GelType gelType, IReadOnlyList<TemperatureZone> zones, bool openBay)
        {
            BayId = DockBayId.New();
            Lots = new FakeGelLotRepository();
            Tasks = new FakeTaskRepository();
            UnitOfWork = new FakeUnitOfWork();
            Metrics = new WarehouseMetrics();
            Bus = new FakeEventBus();

            var clock = new FixedClock(Now);
            var dockScheduler = new DockScheduler();
            // A single wide inbound slot so a receipt at 'now' fits when the bay is open.
            var slot = new DockSlot(Now.AddHours(-1), Now.AddHours(1), DockOperationKind.Inbound);
            dockScheduler.RegisterBay(new DockBay(BayId, openBay, new DockSchedule(new[] { slot })));

            Handler = new RecordInboundGelReceiptHandler(
                new FakeGelTypeCatalog(gelType),
                Lots,
                new FakeZoneRepository(zones),
                Tasks,
                UnitOfWork,
                new VelocityAffinitySlottingStrategy(),
                dockScheduler,
                Metrics,
                Bus,
                clock);
        }

        public DockBayId BayId { get; }
        public FakeGelLotRepository Lots { get; }
        public FakeTaskRepository Tasks { get; }
        public FakeUnitOfWork UnitOfWork { get; }
        public WarehouseMetrics Metrics { get; }
        public FakeEventBus Bus { get; }
        public RecordInboundGelReceiptHandler Handler { get; }
    }

    private sealed class FakeGelTypeCatalog(GelType gelType) : IGelTypeCatalog
    {
        public Task<GelType?> GetByIdAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
            Task.FromResult<GelType?>(gelTypeId.Equals(gelType.Id) ? gelType : null);
    }

    private sealed class FakeGelLotRepository : IGelLotRepository
    {
        public List<GelLot> Added { get; } = new();

        public Task<GelLot?> GetByIdAsync(GelLotId id, CancellationToken ct = default) =>
            Task.FromResult<GelLot?>(Added.Find(l => l.Id.Equals(id)));

        public Task<IReadOnlyList<GelLot>> GetByGelTypeAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(Added.FindAll(l => l.GelTypeId.Equals(gelTypeId)));

        public Task<IReadOnlyList<GelLot>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GelLot>>(Added.ToArray());

        public void Add(GelLot lot) => Added.Add(lot);

        public void Update(GelLot lot) { }
    }

    private sealed class FakeZoneRepository(IReadOnlyList<TemperatureZone> zones) : IZoneRepository
    {
        public Task<TemperatureZone?> GetByIdAsync(ZoneId id, CancellationToken ct = default) =>
            Task.FromResult<TemperatureZone?>(null);

        public Task<IReadOnlyList<TemperatureZone>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult(zones);

        public void Add(TemperatureZone zone) { }

        public void Update(TemperatureZone zone) { }
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        public List<WarehouseTask> Added { get; } = new();

        public Task<WarehouseTask?> GetByIdAsync(WarehouseTaskId id, CancellationToken ct = default) =>
            Task.FromResult<WarehouseTask?>(Added.Find(t => t.Id.Equals(id)));

        public Task<IReadOnlyList<WarehouseTask>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(Added.ToArray());

        public Task<IReadOnlyList<WarehouseTask>> GetUnassignedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(Added.ToArray());

        public void Add(WarehouseTask task) => Added.Add(task);

        public void Update(WarehouseTask task) { }
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

    private sealed class FakeEventBus : IEventBus
    {
        public List<IDomainEvent> Events { get; } = new();

        public bool IsAvailable => true;

        public Task PublishAsync(IDomainEvent @event, CancellationToken ct = default)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IDomainEvent => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
        public ClockMode Mode => ClockMode.Paused;
        public double AccelerationFactor => 1.0;
        public void Configure(ClockMode mode, double accelerationFactor) { }
        public void Pause() { }
        public void Resume() { }
        public TimeSpan Advance(TimeSpan wallDelta) => TimeSpan.Zero;
    }
}
