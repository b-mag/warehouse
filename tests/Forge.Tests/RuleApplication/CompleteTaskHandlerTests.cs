using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Labor;
using Forge.Application.Tasks;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Labor;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
using Xunit;
using TaskStatus = Forge.Domain.Tasks.TaskStatus;

namespace Forge.Tests.RuleApplication;

/// <summary>
/// Unit tests for the Application <see cref="CompleteTaskHandler"/> (task 24.5): completion records the
/// task, accrues labor cost, publishes a <see cref="TaskCompleted"/> event, and persists — while
/// rejections (unknown id, task not in progress) leave every collaborator untouched.
/// Validates: Requirements 8.4, 8.5, 15.3.
/// </summary>
public sealed class CompleteTaskHandlerTests
{
    private static readonly DateTimeOffset Now = new(2350, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Complete_InProgressTask_accrues_cost_publishes_event_and_persists()
    {
        var (handler, ctx) = BuildHandler(hourlyRate: 30m, duration: TimeSpan.FromHours(1), travel: TimeSpan.FromHours(0.5));
        var task = ctx.SeedAssignedInProgressTask();

        var result = await handler.HandleAsync(new CompleteTaskCommand(task.Id));

        Assert.True(result.IsSuccess);
        Assert.Equal(TaskStatus.Completed, task.Status);

        // (1h + 0.5h) * 30 = 45
        Assert.Equal(45m, ctx.Ledger.TotalLaborCost);
        Assert.Equal(45m, ctx.Ledger.UtilizationFor(ctx.WorkerId).LaborCost);

        var published = Assert.Single(ctx.EventBus.Published);
        var completed = Assert.IsType<TaskCompleted>(published);
        Assert.Equal(task.Id, completed.TaskId);
        Assert.Equal(ctx.WorkerId, completed.WorkerId);
        Assert.Equal(45m, completed.LaborCost);
        Assert.Equal(Now, completed.At);

        Assert.Equal(1, ctx.UnitOfWork.SaveCount);
        Assert.Contains(task, ctx.Tasks.Updated);
    }

    [Fact]
    public async Task Complete_unknown_task_is_rejected_and_touches_nothing()
    {
        var (handler, ctx) = BuildHandler(hourlyRate: 30m, duration: TimeSpan.FromHours(1), travel: TimeSpan.Zero);

        var result = await handler.HandleAsync(new CompleteTaskCommand(WarehouseTaskId.New()));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(0m, ctx.Ledger.TotalLaborCost);
        Assert.Empty(ctx.EventBus.Published);
        Assert.Equal(0, ctx.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Complete_task_not_in_progress_is_rejected_leaving_state_unchanged()
    {
        var (handler, ctx) = BuildHandler(hourlyRate: 30m, duration: TimeSpan.FromHours(1), travel: TimeSpan.Zero);
        // Assigned but never Started -> not InProgress; domain Complete() must reject.
        var task = ctx.SeedAssignedTask();

        var result = await handler.HandleAsync(new CompleteTaskCommand(task.Id));

        Assert.True(result.IsFailure);
        Assert.Equal(TaskStatus.Assigned, task.Status);
        Assert.Equal(0m, ctx.Ledger.TotalLaborCost);
        Assert.Empty(ctx.EventBus.Published);
        Assert.Equal(0, ctx.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task Two_identical_completions_accrue_identical_cost_and_running_total()
    {
        var (handler, ctx) = BuildHandler(hourlyRate: 12.5m, duration: TimeSpan.FromMinutes(20), travel: TimeSpan.FromMinutes(10));
        var first = ctx.SeedAssignedInProgressTask();
        var second = ctx.SeedAssignedInProgressTask();

        var r1 = await handler.HandleAsync(new CompleteTaskCommand(first.Id));
        var r2 = await handler.HandleAsync(new CompleteTaskCommand(second.Id));

        Assert.True(r1.IsSuccess);
        Assert.True(r2.IsSuccess);

        // (30 minutes) * 12.5/hr = 6.25 per task -> total 12.5. Both events carry the identical amount.
        var costs = ctx.EventBus.Published.OfType<TaskCompleted>().Select(e => e.LaborCost).ToArray();
        Assert.Equal(2, costs.Length);
        Assert.Equal(costs[0], costs[1]);
        Assert.Equal(6.25m, costs[0]);
        Assert.Equal(12.5m, ctx.Ledger.TotalLaborCost);
    }

    // ---- Test harness ----

    private static (CompleteTaskHandler Handler, TestContext Ctx) BuildHandler(
        decimal hourlyRate, TimeSpan duration, TimeSpan travel)
    {
        var ctx = new TestContext(hourlyRate, duration, travel);
        var handler = new CompleteTaskHandler(
            ctx.Tasks, ctx.Workers, ctx.Ledger, ctx.EventBus, ctx.UnitOfWork, ctx.Clock);
        return (handler, ctx);
    }

    private sealed class TestContext
    {
        public FakeTaskRepository Tasks { get; } = new();
        public FakeWorkerRepository Workers { get; } = new();
        public LaborLedger Ledger { get; } = new();
        public FakeEventBus EventBus { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public FixedClock Clock { get; } = new(Now);
        public WorkerId WorkerId { get; }

        private readonly TimeSpan _duration;
        private readonly TimeSpan _travel;

        public TestContext(decimal hourlyRate, TimeSpan duration, TimeSpan travel)
        {
            _duration = duration;
            _travel = travel;

            var shift = WorkerShift.Create(Now.AddHours(-1), Now.AddHours(8)).Value;
            var worker = Worker.Create(WorkerId.New(), hourlyRate, new[] { shift }).Value;
            WorkerId = worker.Id;
            Workers.Seed(worker);
        }

        public WarehouseTask SeedAssignedTask()
        {
            var task = WarehouseTask.Create(
                WarehouseTaskId.New(), WarehouseTaskType.Pick, new Cell(0, 0), new Cell(1, 1), _duration).Value;
            Assert.True(task.SetTravelTime(_travel).IsSuccess);
            Assert.True(task.AssignTo(WorkerId).IsSuccess);
            Tasks.Seed(task);
            return task;
        }

        public WarehouseTask SeedAssignedInProgressTask()
        {
            var task = SeedAssignedTask();
            Assert.True(task.Start().IsSuccess);
            return task;
        }
    }

    private sealed class FakeTaskRepository : ITaskRepository
    {
        private readonly Dictionary<WarehouseTaskId, WarehouseTask> _byId = new();
        public List<WarehouseTask> Updated { get; } = new();

        public void Seed(WarehouseTask task) => _byId[task.Id] = task;

        public Task<WarehouseTask?> GetByIdAsync(WarehouseTaskId id, CancellationToken ct = default) =>
            Task.FromResult(_byId.TryGetValue(id, out var t) ? t : null);

        public Task<IReadOnlyList<WarehouseTask>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(_byId.Values.ToArray());

        public Task<IReadOnlyList<WarehouseTask>> GetUnassignedAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WarehouseTask>>(Array.Empty<WarehouseTask>());

        public void Add(WarehouseTask task) => _byId[task.Id] = task;
        public void Update(WarehouseTask task) => Updated.Add(task);
    }

    private sealed class FakeWorkerRepository : IWorkerRepository
    {
        private readonly Dictionary<WorkerId, Worker> _byId = new();

        public void Seed(Worker worker) => _byId[worker.Id] = worker;

        public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken ct = default) =>
            Task.FromResult(_byId.TryGetValue(id, out var w) ? w : null);

        public Task<IReadOnlyList<Worker>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Worker>>(_byId.Values.ToArray());

        public Task<IReadOnlyList<Worker>> GetOnShiftAsync(DateTimeOffset at, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Worker>>(_byId.Values.Where(w => w.IsOnShift(at)).ToArray());

        public void Add(Worker worker) => _byId[worker.Id] = worker;
        public void Update(Worker worker) => _byId[worker.Id] = worker;
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
            where TEvent : IDomainEvent => new NoopSubscription();

        private sealed class NoopSubscription : IDisposable
        {
            public void Dispose() { }
        }
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
        public ClockMode Mode => ClockMode.Paused;
        public double AccelerationFactor => 1;
        public void Configure(ClockMode mode, double accelerationFactor) { }
        public void Pause() { }
        public void Resume() { }
        public TimeSpan Advance(TimeSpan wallDelta) => TimeSpan.Zero;
    }
}
