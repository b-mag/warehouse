using System.Collections.Concurrent;
using System.Diagnostics;

using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Contracts.Dtos;
using Forge.Contracts.Events;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Infrastructure.Messaging;
using Forge.Infrastructure.RealTime;

using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit tests for the <see cref="SignalRStatePublisher"/> (task 32.1). The publisher subscribes to the
/// <see cref="IEventBus"/> and, for state-changing domain events, pushes mapped Contracts DTOs through
/// the <see cref="ISimulationClientNotifier"/> seam without ever blocking the tick loop, dropping or
/// queuing pushes while the channel is unavailable so the simulation keeps advancing.
/// Validates: Requirements 20.9, 23.2, 23.4, 23.5.
/// </summary>
public sealed class SignalRStatePublisherTests
{
    /// <summary>A fake notifier that records every push and signals when the expected count arrives.</summary>
    private sealed class CapturingNotifier : ISimulationClientNotifier
    {
        private readonly ConcurrentQueue<(string EventName, object Payload)> _pushes = new();
        private readonly int _expected;
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturingNotifier(int expected) => _expected = expected;

        public IReadOnlyList<(string EventName, object Payload)> Pushes => _pushes.ToArray();

        public Task NotifyAsync(string eventName, object payload, CancellationToken ct = default)
        {
            _pushes.Enqueue((eventName, payload));
            if (_pushes.Count >= _expected)
            {
                _reached.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public Task WaitForPushesAsync() => _reached.Task;
    }

    /// <summary>A notifier that blocks forever, simulating a slow/unavailable channel.</summary>
    private sealed class BlockingNotifier : ISimulationClientNotifier
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NotifyAsync(string eventName, object payload, CancellationToken ct = default) => _release.Task;
    }

    /// <summary>A notifier that always faults, simulating a genuinely unavailable channel.</summary>
    private sealed class ThrowingNotifier : ISimulationClientNotifier
    {
        public Task NotifyAsync(string eventName, object payload, CancellationToken ct = default) =>
            throw new InvalidOperationException("channel unavailable");
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int ms = 5000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(ms));
        Assert.True(completed == task, "operation timed out");
        return await task;
    }

    private static async Task WithTimeout(Task task, int ms = 5000)
    {
        var completed = await Task.WhenAny(task, Task.Delay(ms));
        Assert.True(completed == task, "operation timed out");
        await task;
    }

    // ---- A state-changing event results in a push carrying the correct DTO (Req 23.2, 23.4) ----

    [Fact]
    public async Task State_changing_event_pushes_mapped_contracts_dto()
    {
        var bus = new InProcessEventBus();
        var notifier = new CapturingNotifier(expected: 1);
        await using var publisher = new SignalRStatePublisher(bus, notifier);

        var lotId = GelLotId.New();
        var at = DateTimeOffset.UnixEpoch.AddHours(3);
        await bus.PublishAsync(new LotExpired(lotId, at));

        await WithTimeout(notifier.WaitForPushesAsync());

        var push = Assert.Single(notifier.Pushes);
        Assert.Equal("LotExpired", push.EventName);
        var dto = Assert.IsType<LotExpiredEvent>(push.Payload);
        Assert.Equal(lotId.Value, dto.LotId);
        Assert.Equal(at, dto.At);
    }

    [Fact]
    public async Task Starship_loading_event_pushes_mapped_contracts_dto()
    {
        var bus = new InProcessEventBus();
        var notifier = new CapturingNotifier(expected: 1);
        await using var publisher = new SignalRStatePublisher(bus, notifier);

        var shipId = StarshipId.New();
        await bus.PublishAsync(new LoadingWindowClosed(shipId, Loaded: 40, Shortfall: 5, DateTimeOffset.UnixEpoch));

        await WithTimeout(notifier.WaitForPushesAsync());

        var push = Assert.Single(notifier.Pushes);
        Assert.Equal("LoadingWindowClosed", push.EventName);
        var dto = Assert.IsType<LoadingWindowClosedEvent>(push.Payload);
        Assert.Equal(shipId.Value, dto.StarshipId);
        Assert.Equal(40, dto.Loaded);
        Assert.Equal(5, dto.Shortfall);
    }

    // ---- An OperatorParameterChanged event pushes the updated parameter state (Req 20.9) ----

    [Fact]
    public async Task OperatorParameterChanged_pushes_updated_parameter_state()
    {
        var bus = new InProcessEventBus();
        var notifier = new CapturingNotifier(expected: 1);
        await using var publisher = new SignalRStatePublisher(bus, notifier);

        var state = new OperatorParameterStateDto(
            SimSpeed: 2.5,
            WorkersOnShift: 12,
            OpenDockBays: 4,
            InboundRate: 7.0,
            DemandMultiplier: 1.5,
            SlottingStrategy: "velocity");
        await bus.PublishAsync(new OperatorParameterChanged(state, DateTimeOffset.UnixEpoch));

        await WithTimeout(notifier.WaitForPushesAsync());

        var push = Assert.Single(notifier.Pushes);
        Assert.Equal("OperatorParameterChanged", push.EventName);
        var evt = Assert.IsType<OperatorParameterChangedEvent>(push.Payload);
        Assert.Same(state, evt.State);
    }

    // ---- Publishing does not block / does not throw when the channel is unavailable (Req 23.5) ----

    [Fact]
    public async Task Publish_returns_promptly_when_channel_is_blocked()
    {
        var bus = new InProcessEventBus();
        var notifier = new BlockingNotifier();
        await using var publisher = new SignalRStatePublisher(bus, notifier);

        // Even though the notifier never completes a push, publishing many events must not block the
        // publishing thread: DropOldest keeps the writer from ever back-pressuring the bus (Req 23.5).
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5000; i++)
        {
            await bus.PublishAsync(new BacklogChanged("inbound", i, DateTimeOffset.UnixEpoch));
        }

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 4000, $"publishing blocked for {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Publish_does_not_throw_when_delivery_faults()
    {
        var bus = new InProcessEventBus();
        var notifier = new ThrowingNotifier();
        await using var publisher = new SignalRStatePublisher(bus, notifier);

        // The pump swallows the delivery fault; the publish path never sees it and the simulation
        // keeps advancing (Req 23.5).
        var ex = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                await bus.PublishAsync(new BacklogChanged("outbound", i, DateTimeOffset.UnixEpoch));
            }
        });

        Assert.Null(ex);

        // The publisher is still alive and continues to accept pushes.
        await bus.PublishAsync(new DockBlocked(DockBayId.New(), DateTimeOffset.UnixEpoch));
    }

    // ---- Disposal unsubscribes so no further pushes are delivered (lifecycle) ----

    [Fact]
    public async Task Disposal_stops_further_pushes()
    {
        var bus = new InProcessEventBus();
        var notifier = new CapturingNotifier(expected: 1);
        var publisher = new SignalRStatePublisher(bus, notifier);

        await bus.PublishAsync(new BacklogChanged("inbound", 1, DateTimeOffset.UnixEpoch));
        await WithTimeout(notifier.WaitForPushesAsync());
        var countAfterFirst = notifier.Pushes.Count;

        await publisher.DisposeAsync();

        // After disposal the subscription is gone, so this event reaches no handler.
        await bus.PublishAsync(new BacklogChanged("inbound", 2, DateTimeOffset.UnixEpoch));
        await Task.Delay(100);

        Assert.Equal(countAfterFirst, notifier.Pushes.Count);
    }
}
