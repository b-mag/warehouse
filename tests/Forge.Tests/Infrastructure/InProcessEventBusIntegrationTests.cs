using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Infrastructure.Messaging;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Integration-flavored end-to-end tests for the Phase-1 <see cref="InProcessEventBus"/> (task 30.2).
/// Where the sibling unit tests (task 30.1) each isolate one behavior, these exercise a full
/// operational sequence against real <see cref="IDomainEvent"/> records: fan-out delivery to multiple
/// heterogeneous subscribers (Req 27.3), a transition into the degraded/unavailable state during which
/// several published events are retained rather than delivered (Req 27.5), and recovery that drains
/// the retained events to subscribers in their original publish order before newly published events
/// flow again (Req 27.5). This mirrors the simulation lifecycle in which the tick loop publishes a
/// stream of domain events through the bus (Req 28.2).
///
/// Determinism: every publish and the recovery drain are awaited directly, so there are no timers,
/// sleeps, or polling — assertions observe a fully settled bus.
///
/// Validates: Requirements 27.3, 27.5, 28.2.
/// </summary>
public sealed class InProcessEventBusIntegrationTests
{
    private static LotExpired LotExpiredAt(DateTimeOffset at) => new(GelLotId.New(), at);

    private static TemperatureExcursion ExcursionAt(DateTimeOffset at) =>
        new(GelLotId.New(), Celsius: -70m, at);

    private static DockBlocked DockBlockedAt(DateTimeOffset at) => new(DockBayId.New(), at);

    /// <summary>
    /// End-to-end fan-out: a single published domain event reaches every subscriber registered for
    /// its concrete type as well as a subscriber registered for the <see cref="IDomainEvent"/> marker,
    /// while a subscriber for an unrelated event type is left untouched (Req 27.3).
    /// </summary>
    [Fact]
    public async Task Publish_fans_out_a_real_domain_event_to_all_matching_subscribers()
    {
        var bus = new InProcessEventBus();

        var lotSubscriberA = new List<LotExpired>();
        var lotSubscriberB = new List<LotExpired>();
        var everyEvent = new List<IDomainEvent>();
        var unrelated = new List<DockBlocked>();

        bus.Subscribe<LotExpired>((e, _) => { lotSubscriberA.Add(e); return Task.CompletedTask; });
        bus.Subscribe<LotExpired>((e, _) => { lotSubscriberB.Add(e); return Task.CompletedTask; });
        bus.Subscribe<IDomainEvent>((e, _) => { everyEvent.Add(e); return Task.CompletedTask; });
        bus.Subscribe<DockBlocked>((e, _) => { unrelated.Add(e); return Task.CompletedTask; });

        var evt = LotExpiredAt(DateTimeOffset.UnixEpoch);
        await bus.PublishAsync(evt);

        Assert.Same(evt, Assert.Single(lotSubscriberA));
        Assert.Same(evt, Assert.Single(lotSubscriberB));
        Assert.Same(evt, Assert.Single(everyEvent));
        Assert.Empty(unrelated);
    }

    /// <summary>
    /// The full degraded-then-recover lifecycle over a heterogeneous stream of real domain events:
    /// events published while available deliver immediately; once the bus is marked unavailable the
    /// subsequent events are retained (not delivered) and <see cref="InProcessEventBus.RetainedEventCount"/>
    /// reflects the buffered count and the bus reports the degraded state (Req 27.5); on recovery the
    /// retained events drain to subscribers in their original publish order, and a further publish is
    /// delivered live (Req 27.3, 27.5, 28.2).
    /// </summary>
    [Fact]
    public async Task Degraded_retains_a_stream_then_recovery_drains_it_in_publish_order()
    {
        var bus = new InProcessEventBus();

        // A marker subscriber records the delivery order across ALL domain event types so we can
        // assert the retained stream drains in the exact order it was published.
        var deliveredOrder = new List<IDomainEvent>();
        bus.Subscribe<IDomainEvent>((e, _) => { deliveredOrder.Add(e); return Task.CompletedTask; });

        var t0 = DateTimeOffset.UnixEpoch;

        // 1) Available: delivered immediately.
        var live = LotExpiredAt(t0);
        await bus.PublishAsync(live);
        Assert.Same(live, Assert.Single(deliveredOrder));
        Assert.True(bus.IsAvailable);
        Assert.Equal(0, bus.RetainedEventCount);

        // 2) Degrade the bus and publish a mixed stream; nothing new should be delivered.
        bus.SetAvailable(false);
        Assert.False(bus.IsAvailable);

        var r0 = LotExpiredAt(t0.AddMinutes(1));
        var r1 = ExcursionAt(t0.AddMinutes(2));
        var r2 = DockBlockedAt(t0.AddMinutes(3));
        var r3 = LotExpiredAt(t0.AddMinutes(4));

        await bus.PublishAsync(r0);
        await bus.PublishAsync(r1);
        await bus.PublishAsync(r2);
        await bus.PublishAsync(r3);

        Assert.Single(deliveredOrder);           // still only the pre-degrade event
        Assert.Equal(4, bus.RetainedEventCount); // all four retained in order
        Assert.False(bus.IsAvailable);

        // 3) Recover: retained events drain in original publish order, before any new publish.
        await bus.RecoverAsync();

        Assert.True(bus.IsAvailable);
        Assert.Equal(0, bus.RetainedEventCount);
        Assert.Equal(new IDomainEvent[] { live, r0, r1, r2, r3 }, deliveredOrder);

        // 4) A post-recovery publish is delivered live again.
        var afterRecovery = ExcursionAt(t0.AddMinutes(5));
        await bus.PublishAsync(afterRecovery);

        Assert.Equal(new IDomainEvent[] { live, r0, r1, r2, r3, afterRecovery }, deliveredOrder);
        Assert.Equal(0, bus.RetainedEventCount);
    }

    /// <summary>
    /// Retained events are dispatched to the subscribers present at recovery time: a subscriber that
    /// registers only for the retained event's concrete type receives exactly the retained events of
    /// that type, confirming that retention preserves the event instances for later type-matched
    /// delivery rather than dropping them (Req 27.5).
    /// </summary>
    [Fact]
    public async Task Recovery_delivers_retained_events_to_type_matched_subscribers()
    {
        var bus = new InProcessEventBus();

        var lots = new List<LotExpired>();
        var excursions = new List<TemperatureExcursion>();
        bus.Subscribe<LotExpired>((e, _) => { lots.Add(e); return Task.CompletedTask; });
        bus.Subscribe<TemperatureExcursion>((e, _) => { excursions.Add(e); return Task.CompletedTask; });

        var t0 = DateTimeOffset.UnixEpoch;
        var lotA = LotExpiredAt(t0);
        var excursion = ExcursionAt(t0.AddMinutes(1));
        var lotB = LotExpiredAt(t0.AddMinutes(2));

        bus.SetAvailable(false);
        await bus.PublishAsync(lotA);
        await bus.PublishAsync(excursion);
        await bus.PublishAsync(lotB);

        Assert.Empty(lots);
        Assert.Empty(excursions);
        Assert.Equal(3, bus.RetainedEventCount);

        await bus.RecoverAsync();

        Assert.Equal(new[] { lotA, lotB }, lots);
        Assert.Same(excursion, Assert.Single(excursions));
        Assert.Equal(0, bus.RetainedEventCount);
    }
}
