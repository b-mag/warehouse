using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Infrastructure.Messaging;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit tests for the Phase-1 <see cref="InProcessEventBus"/> (task 30.1): an in-process
/// publish/subscribe dispatcher that delivers domain events to matching subscribers, honors
/// subscription disposal, and — when degraded — retains published events in order and drains them on
/// recovery while reporting the degraded state through <see cref="InProcessEventBus.IsAvailable"/>.
/// Validates: Requirements 27.1, 27.3, 27.4, 27.5.
/// </summary>
public sealed class InProcessEventBusTests
{
    private static LotExpired Lot(DateTimeOffset at) => new(GelLotId.New(), at);

    // ---- Availability (Req 27.1) ----

    [Fact]
    public void IsAvailable_is_true_by_default()
    {
        Assert.True(new InProcessEventBus().IsAvailable);
        Assert.Equal(0, new InProcessEventBus().RetainedEventCount);
    }

    // ---- Publish/subscribe dispatch (Req 27.3, 27.4) ----

    [Fact]
    public async Task PublishAsync_delivers_to_matching_subscriber()
    {
        var bus = new InProcessEventBus();
        var received = new List<LotExpired>();
        bus.Subscribe<LotExpired>((e, _) =>
        {
            received.Add(e);
            return Task.CompletedTask;
        });

        var evt = Lot(DateTimeOffset.UnixEpoch);
        await bus.PublishAsync(evt);

        Assert.Single(received);
        Assert.Same(evt, received[0]);
    }

    [Fact]
    public async Task PublishAsync_delivers_to_all_subscribers_of_the_event_type()
    {
        var bus = new InProcessEventBus();
        var count = 0;
        bus.Subscribe<LotExpired>((_, _) => { count++; return Task.CompletedTask; });
        bus.Subscribe<LotExpired>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task PublishAsync_does_not_deliver_to_subscribers_of_a_different_event_type()
    {
        var bus = new InProcessEventBus();
        var lotHits = 0;
        var dockHits = 0;
        bus.Subscribe<LotExpired>((_, _) => { lotHits++; return Task.CompletedTask; });
        bus.Subscribe<DockBlocked>((_, _) => { dockHits++; return Task.CompletedTask; });

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(1, lotHits);
        Assert.Equal(0, dockHits);
    }

    [Fact]
    public async Task Subscribing_to_the_marker_interface_receives_every_domain_event()
    {
        var bus = new InProcessEventBus();
        var all = new List<IDomainEvent>();
        bus.Subscribe<IDomainEvent>((e, _) => { all.Add(e); return Task.CompletedTask; });

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));
        await bus.PublishAsync(new DockBlocked(default, DateTimeOffset.UnixEpoch));

        Assert.Equal(2, all.Count);
    }

    // ---- Disposal (Req 27.3) ----

    [Fact]
    public async Task Disposed_subscription_no_longer_receives_events()
    {
        var bus = new InProcessEventBus();
        var count = 0;
        var subscription = bus.Subscribe<LotExpired>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));
        subscription.Dispose();
        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Disposing_one_subscription_leaves_others_intact()
    {
        var bus = new InProcessEventBus();
        var kept = 0;
        var dropped = 0;
        var toDispose = bus.Subscribe<LotExpired>((_, _) => { dropped++; return Task.CompletedTask; });
        bus.Subscribe<LotExpired>((_, _) => { kept++; return Task.CompletedTask; });

        toDispose.Dispose();
        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(0, dropped);
        Assert.Equal(1, kept);
    }

    [Fact]
    public void Disposing_a_subscription_twice_is_safe()
    {
        var bus = new InProcessEventBus();
        var subscription = bus.Subscribe<LotExpired>((_, _) => Task.CompletedTask);

        subscription.Dispose();
        subscription.Dispose();
    }

    // ---- Degraded mode + recovery (Req 27.5) ----

    [Fact]
    public async Task Degraded_bus_reports_unavailable_and_retains_events_without_delivering()
    {
        var bus = new InProcessEventBus();
        var delivered = 0;
        bus.Subscribe<LotExpired>((_, _) => { delivered++; return Task.CompletedTask; });

        bus.SetAvailable(false);
        Assert.False(bus.IsAvailable);

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));
        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(0, delivered);
        Assert.Equal(2, bus.RetainedEventCount);
    }

    [Fact]
    public async Task Recovery_drains_retained_events_in_publish_order()
    {
        var bus = new InProcessEventBus();
        var received = new List<DateTimeOffset>();
        bus.Subscribe<LotExpired>((e, _) => { received.Add(e.At); return Task.CompletedTask; });

        var t0 = DateTimeOffset.UnixEpoch;
        var t1 = t0.AddMinutes(1);
        var t2 = t0.AddMinutes(2);

        bus.SetAvailable(false);
        await bus.PublishAsync(Lot(t0));
        await bus.PublishAsync(Lot(t1));
        await bus.PublishAsync(Lot(t2));

        await bus.RecoverAsync();

        Assert.Equal(new[] { t0, t1, t2 }, received);
        Assert.True(bus.IsAvailable);
        Assert.Equal(0, bus.RetainedEventCount);
    }

    [Fact]
    public async Task Events_published_after_recovery_are_delivered_normally()
    {
        var bus = new InProcessEventBus();
        var count = 0;
        bus.Subscribe<LotExpired>((_, _) => { count++; return Task.CompletedTask; });

        bus.SetAvailable(false);
        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));
        await bus.RecoverAsync();

        await bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch));

        Assert.Equal(2, count);
        Assert.Equal(0, bus.RetainedEventCount);
    }

    [Fact]
    public async Task Recovery_with_no_retained_events_just_restores_availability()
    {
        var bus = new InProcessEventBus();
        bus.SetAvailable(false);

        await bus.RecoverAsync();

        Assert.True(bus.IsAvailable);
        Assert.Equal(0, bus.RetainedEventCount);
    }

    // ---- Concurrency (thread-safety) ----

    [Fact]
    public async Task Concurrent_publishers_all_deliver_to_a_subscriber()
    {
        var bus = new InProcessEventBus();
        var count = 0;
        bus.Subscribe<LotExpired>((_, _) => { Interlocked.Increment(ref count); return Task.CompletedTask; });

        const int publishers = 50;
        var tasks = Enumerable.Range(0, publishers)
            .Select(_ => Task.Run(() => bus.PublishAsync(Lot(DateTimeOffset.UnixEpoch))))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(publishers, count);
    }
}
