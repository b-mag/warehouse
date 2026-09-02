using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Xunit;

namespace Forge.Tests.OperatorParameters;

/// <summary>
/// Unit tests for the Application <see cref="UpdateOperatorParameterHandler"/> (task 24.7): a valid
/// change is applied to the live state and an <see cref="OperatorParameterChanged"/> event carrying
/// the updated state is published (Req 20.9); an invalid/out-of-range change is rejected with the
/// previous value retained and nothing published (Req 20.8).
/// <para>Validates: Requirements 20.8, 20.9.</para>
/// </summary>
public sealed class UpdateOperatorParameterHandlerTests
{
    private static readonly DateTimeOffset Now = new(2350, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private const int WorkerMax = 10;
    private const int ModeledDockBays = 4;

    [Fact]
    public async Task Valid_change_applies_to_live_state_and_publishes_updated_state()
    {
        var (handler, ctx) = BuildHandler();

        // Move workers-on-shift from its initial value (WorkerMax) to a distinct valid value.
        var result = await handler.Handle(
            new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, "3"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        // Applied to live state (Req 20.8 accept path).
        Assert.Equal(3, ctx.State.WorkersOnShift);

        // Exactly one updated-state event published, carrying the post-change state (Req 20.9).
        var published = Assert.Single(ctx.Bus.Published);
        var changed = Assert.IsType<OperatorParameterChanged>(published);
        Assert.Equal(3, changed.State.WorkersOnShift);
        Assert.Equal(ctx.State.ToDto(), changed.State);
        Assert.Equal(Now, changed.OccurredAt);
    }

    [Fact]
    public async Task Valid_slotting_strategy_change_publishes_the_new_strategy()
    {
        var (handler, ctx) = BuildHandler();

        var result = await handler.Handle(
            new OperatorParameterDto(OperatorParameterKey.SlottingStrategy, SlottingStrategyKey.NaiveFirstAvailable));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(SlottingStrategyKey.NaiveFirstAvailable, ctx.State.SlottingStrategy);

        var changed = Assert.IsType<OperatorParameterChanged>(Assert.Single(ctx.Bus.Published));
        Assert.Equal(SlottingStrategyKey.NaiveFirstAvailable, changed.State.SlottingStrategy);
    }

    [Fact]
    public async Task Out_of_range_change_is_rejected_retains_previous_value_and_publishes_nothing()
    {
        var (handler, ctx) = BuildHandler();
        var previousWorkers = ctx.State.WorkersOnShift; // == WorkerMax by default

        // WorkerMax + 1 is above the configured maximum -> out of range (Req 20.8).
        var result = await handler.Handle(
            new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, (WorkerMax + 1).ToString()));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        // Error identifies the invalid parameter (Req 20.8).
        Assert.Equal(OperatorParameterKey.WorkersOnShift, GetParameter(result.Error));

        // Previous value retained.
        Assert.Equal(previousWorkers, ctx.State.WorkersOnShift);
        // Nothing published for a rejected change.
        Assert.Empty(ctx.Bus.Published);
    }

    [Fact]
    public async Task Invalid_type_change_is_rejected_and_publishes_nothing()
    {
        var (handler, ctx) = BuildHandler();

        // A non-numeric value for a numeric parameter is a wrong-type rejection (Req 20.8).
        var result = await handler.Handle(
            new OperatorParameterDto(OperatorParameterKey.SimSpeed, "not-a-number"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal(OperatorParameterKey.SimSpeed, GetParameter(result.Error));
        Assert.Empty(ctx.Bus.Published);
    }

    [Fact]
    public async Task Unknown_parameter_key_is_rejected_and_publishes_nothing()
    {
        var (handler, ctx) = BuildHandler();

        var result = await handler.Handle(new OperatorParameterDto("no-such-parameter", "1"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Empty(ctx.Bus.Published);
    }

    private static string? GetParameter(DomainError error) =>
        error.Detail is not null && error.Detail.TryGetValue("parameter", out var p) ? p as string : null;

    private static (UpdateOperatorParameterHandler Handler, Context Ctx) BuildHandler()
    {
        var state = new OperatorParameterState(new OperatorParameterOptions
        {
            WorkerMax = WorkerMax,
            ModeledDockBays = ModeledDockBays,
        });
        var service = new OperatorParameterService(state);
        var bus = new FakeEventBus();
        var clock = new FixedClock(Now);
        var handler = new UpdateOperatorParameterHandler(service, bus, clock);
        return (handler, new Context(state, bus, clock));
    }

    private sealed class Context
    {
        public Context(OperatorParameterState state, FakeEventBus bus, FixedClock clock)
        {
            State = state;
            Bus = bus;
            Clock = clock;
        }

        public OperatorParameterState State { get; }
        public FakeEventBus Bus { get; }
        public FixedClock Clock { get; }
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
