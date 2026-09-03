using Forge.Api.Controllers;
using Forge.Api.Forecasting;
using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Application.Forecasting;
using Forge.Application.OperatorParameters;
using Forge.Contracts.Dtos;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Microsoft.AspNetCore.Mvc;

namespace Forge.Tests.Api;

/// <summary>
/// Unit tests for the Forge.Api REST controllers (task 33.2; Req 9.1, 9.3, 20.1, 20.8, 22.1–22.4).
/// Each controller is exercised over fake handlers / gateway / stores so only the controller's own
/// request-mapping and Result-&gt;HTTP mapping behavior is under test.
/// </summary>
public sealed class ControllersTests
{
    // ---------------------------------------------------------------- OrdersController

    [Fact]
    public async Task Orders_Create_returns_201_with_new_order_id_on_success()
    {
        var newId = ColonyOrderId.New();
        var gateway = new FakeGateway { CreateResult = Result.Success(newId) };
        var controller = new OrdersController(gateway);

        var request = new CreateColonyOrderRequest(
            ColonyId: Guid.NewGuid(),
            Lines: new[] { new CreateColonyOrderLineRequest(Guid.NewGuid(), 3) },
            DeliveryWindowStart: DateTimeOffset.UnixEpoch,
            DeliveryWindowEnd: DateTimeOffset.UnixEpoch.AddHours(1));

        var result = await controller.CreateAsync(request, default);

        var created = Assert.IsType<CreatedResult>(result.Result);
        var body = Assert.IsType<CreateColonyOrderResponse>(created.Value);
        Assert.Equal(newId.Value, body.OrderId);

        // The command was mapped through to the gateway.
        Assert.NotNull(gateway.LastCommand);
        Assert.Equal(request.ColonyId, gateway.LastCommand!.ColonyId.Value);
        Assert.Single(gateway.LastCommand.Lines);
        Assert.Equal(3, gateway.LastCommand.Lines[0].Quantity);
    }

    [Fact]
    public async Task Orders_Create_maps_validation_failure_to_400_with_detail()
    {
        var error = DomainError.Validation("Quantity must be >= 1.", "Quantity");
        var gateway = new FakeGateway { CreateResult = Result.Failure<ColonyOrderId>(error) };
        var controller = new OrdersController(gateway);

        var request = new CreateColonyOrderRequest(
            ColonyId: Guid.NewGuid(),
            Lines: new[] { new CreateColonyOrderLineRequest(Guid.NewGuid(), 0) },
            DeliveryWindowStart: DateTimeOffset.UnixEpoch,
            DeliveryWindowEnd: DateTimeOffset.UnixEpoch.AddHours(1));

        var result = await controller.CreateAsync(request, default);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, problem.StatusCode);
        var body = Assert.IsType<ApiErrorDto>(problem.Value);
        Assert.Equal(ErrorKind.Validation.ToString(), body.Kind);
        Assert.NotNull(body.Detail);
        Assert.Equal("Quantity", body.Detail!["parameter"]);
    }

    // ------------------------------------------------ OperatorParametersController

    [Fact]
    public void OperatorParameters_Get_returns_the_current_state_dto()
    {
        var (state, handler) = BuildOperatorParameters();
        var controller = new OperatorParametersController(state, handler);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<OperatorParameterStateDto>(ok.Value);
        Assert.Equal(state.SimSpeed, dto.SimSpeed);
        Assert.Equal(state.WorkersOnShift, dto.WorkersOnShift);
        Assert.Equal(state.OpenDockBays, dto.OpenDockBays);
        Assert.Equal(state.SlottingStrategy, dto.SlottingStrategy);
    }

    [Fact]
    public async Task OperatorParameters_Update_applies_valid_change_and_returns_updated_state()
    {
        var (state, handler) = BuildOperatorParameters();
        var controller = new OperatorParametersController(state, handler);

        var change = new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, "3");
        var result = await controller.UpdateAsync(change, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<OperatorParameterStateDto>(ok.Value);
        Assert.Equal(3, dto.WorkersOnShift);
        Assert.Equal(3, state.WorkersOnShift);
    }

    [Fact]
    public async Task OperatorParameters_Update_maps_invalid_value_to_400_naming_the_parameter()
    {
        var (state, handler) = BuildOperatorParameters();
        var controller = new OperatorParametersController(state, handler);

        // WorkerMax is 5 in the fixture; 99 is out of range → rejected, previous value retained.
        var change = new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, "99");
        var result = await controller.UpdateAsync(change, default);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, problem.StatusCode);
        var body = Assert.IsType<ApiErrorDto>(problem.Value);
        Assert.Equal(ErrorKind.Validation.ToString(), body.Kind);
        Assert.NotNull(body.Detail);
        Assert.Equal(OperatorParameterKey.WorkersOnShift, body.Detail!["parameter"]);

        // Previous value retained (Req 20.8).
        Assert.Equal(5, state.WorkersOnShift);
    }

    // ------------------------------------------------------------- ForecastController

    [Fact]
    public void Forecast_Get_exposes_produced_forecasts_for_review()
    {
        var store = new InMemoryForecastReviewStore();
        var id = store.Add(ForecastLifecycle.Pending(SampleForecast()));
        var controller = new ForecastController(store, BuildForecastHandler());

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ForecastDecisionResponse[]>(ok.Value);
        var item = Assert.Single(body);
        Assert.Equal(id, item.ForecastId);
        Assert.Equal(ForecastState.Pending.ToWireName(), item.State);
    }

    [Fact]
    public async Task Forecast_accept_settles_forecast_to_accepted()
    {
        var store = new InMemoryForecastReviewStore();
        var id = Guid.NewGuid();
        store.Set(id, ForecastLifecycle.Pending(SampleForecast()));
        var controller = new ForecastController(store, BuildForecastHandler());

        var request = new SubmitForecastDecisionRequest(id, "Accept", "operator-1");
        var result = await controller.SubmitDecisionAsync(request, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ForecastDecisionResponse>(ok.Value);
        Assert.Equal(ForecastState.Accepted.ToWireName(), body.State);

        Assert.True(store.TryGet(id, out var settled));
        Assert.Equal(ForecastState.Accepted, settled.State);
    }

    [Fact]
    public async Task Forecast_override_with_valid_value_settles_to_overridden()
    {
        var store = new InMemoryForecastReviewStore();
        var id = Guid.NewGuid();
        store.Set(id, ForecastLifecycle.Pending(SampleForecast()));
        var audit = new FakeAuditSink();
        var controller = new ForecastController(store, BuildForecastHandler(audit));

        var request = new SubmitForecastDecisionRequest(id, "Override", "operator-1", "42");
        var result = await controller.SubmitDecisionAsync(request, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ForecastDecisionResponse>(ok.Value);
        Assert.Equal(ForecastState.Overridden.ToWireName(), body.State);
        Assert.Equal(42L, body.Forecast.Quantity);
        Assert.Equal(1, audit.RecordedCount);
    }

    [Fact]
    public async Task Forecast_override_with_invalid_value_maps_to_400()
    {
        var store = new InMemoryForecastReviewStore();
        var id = Guid.NewGuid();
        store.Set(id, ForecastLifecycle.Pending(SampleForecast()));
        var controller = new ForecastController(store, BuildForecastHandler());

        var request = new SubmitForecastDecisionRequest(id, "Override", "operator-1", "not-a-number");
        var result = await controller.SubmitDecisionAsync(request, default);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, problem.StatusCode);
        var body = Assert.IsType<ApiErrorDto>(problem.Value);
        Assert.Equal(ErrorKind.Validation.ToString(), body.Kind);

        // Original forecast retained unchanged (Req 22.4).
        Assert.True(store.TryGet(id, out var unchanged));
        Assert.Equal(ForecastState.Pending, unchanged.State);
    }

    [Fact]
    public async Task Forecast_decision_on_unknown_id_returns_404()
    {
        var store = new InMemoryForecastReviewStore();
        var controller = new ForecastController(store, BuildForecastHandler());

        var request = new SubmitForecastDecisionRequest(Guid.NewGuid(), "Accept", "operator-1");
        var result = await controller.SubmitDecisionAsync(request, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---------------------------------------------------------------- QueryController

    [Fact]
    public async Task Query_snapshot_returns_the_current_snapshot()
    {
        var snapshot = SampleSnapshot();
        var gateway = new FakeGateway { Snapshot = snapshot };
        var controller = new QueryController(gateway);

        var result = await controller.GetSnapshotAsync(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(snapshot, ok.Value);
    }

    // ---------------------------------------------------------------- Fixtures / fakes

    private static (OperatorParameterState State, UpdateOperatorParameterHandler Handler) BuildOperatorParameters()
    {
        var options = new OperatorParameterOptions { WorkerMax = 5, ModeledDockBays = 4 };
        var state = new OperatorParameterState(options);
        var service = new OperatorParameterService(state);
        var handler = new UpdateOperatorParameterHandler(service, new FakeEventBus(), new FixedClock());
        return (state, handler);
    }

    private static SubmitForecastDecisionHandler BuildForecastHandler(IForecastAuditSink? audit = null) =>
        new(new FixedClock(), audit ?? new FakeAuditSink());

    private static DemandForecast SampleForecast() =>
        new(ColonyId.New(), GelTypeId.New(), TimeSpan.FromHours(24), ExpectedDemand: 10d, IsFallback: false);

    private static SimulationSnapshotDto SampleSnapshot() =>
        new(
            Zones: Array.Empty<TemperatureZoneDto>(),
            Lots: Array.Empty<GelLotDto>(),
            Agents: Array.Empty<AgentDto>(),
            Starships: Array.Empty<StarshipDto>(),
            Metrics: new BacklogMetricsDto(0, 0, 0d, 0d, 0, 0d),
            Parameters: new OperatorParameterStateDto(1.0, 5, 4, 1.0, 1.0, "velocity-affinity"));

    private sealed class FakeGateway : IWarehouseCommandGateway
    {
        public Result<ColonyOrderId> CreateResult { get; set; } = Result.Success(ColonyOrderId.New());
        public SimulationSnapshotDto? Snapshot { get; set; }
        public CreateColonyOrderCommand? LastCommand { get; private set; }

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(CreateColonyOrderCommand cmd, CancellationToken ct = default)
        {
            LastCommand = cmd;
            return Task.FromResult(CreateResult);
        }

        public Task<Result> RecordInboundGelReceiptAsync(RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RecordTemperatureReadingAsync(RecordTemperatureReadingCommand cmd, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(Snapshot ?? SampleSnapshot());
    }

    private sealed class FakeEventBus : IEventBus
    {
        public bool IsAvailable => true;
        public Task PublishAsync(IDomainEvent @event, CancellationToken ct = default) => Task.CompletedTask;
        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeAuditSink : IForecastAuditSink
    {
        public int RecordedCount { get; private set; }
        public Task RecordOverrideAsync(PredictionOverrideAudit audit, CancellationToken ct = default)
        {
            RecordedCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
        public ClockMode Mode => ClockMode.RealTime;
        public double AccelerationFactor => 1.0;
        public void Configure(ClockMode mode, double accelerationFactor) { }
        public void Pause() { }
        public void Resume() { }
        public TimeSpan Advance(TimeSpan wallDelta) => TimeSpan.Zero;
    }
}
