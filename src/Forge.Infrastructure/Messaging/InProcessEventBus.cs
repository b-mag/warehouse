using System.Collections.Concurrent;

using Forge.Application.Abstractions;
using Forge.Domain.Events;

namespace Forge.Infrastructure.Messaging;

/// <summary>
/// Phase-1 in-process implementation of <see cref="IEventBus"/> (Req 27.1, design "Messaging").
/// It is a synchronous-dispatch publish/subscribe dispatcher: <see cref="PublishAsync"/> delivers
/// each <see cref="IDomainEvent"/> to every handler subscribed to a runtime-compatible event type
/// (Req 27.3, 27.4 — the events dispatched are the domain event instances as-is; a boundary mapper
/// elsewhere translates them to the Contracts schemas). A RabbitMQ/MassTransit transport can replace
/// this class in Phase 2 behind the same abstraction without touching the Application layer (Req 27.2).
///
/// <para><b>Degraded mode (Req 27.5).</b> When the bus is marked unavailable (see
/// <see cref="SetAvailable"/>), published events are not dropped: they are appended to an ordered
/// retention buffer and <see cref="IsAvailable"/> reports the degraded state. When the bus recovers,
/// the buffered events are drained and dispatched in their original publish order before any newly
/// published event is delivered.</para>
///
/// <para><b>Thread-safety.</b> Publishing (driven by the tick loop) and subscribing (driven by
/// clients such as the SignalR publisher) may run concurrently. The subscriber registry is a
/// concurrent, copy-on-iterate structure, and handlers are always invoked outside any lock so a slow
/// or re-entrant handler can never deadlock a publisher. A single retention lock serializes the
/// small critical sections that append to and drain the degraded-mode buffer.</para>
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    /// <summary>
    /// Subscriptions keyed by the exact <see cref="IDomainEvent"/> type they were registered for.
    /// Each bucket is a concurrent map from a unique token to its typed dispatch delegate so that
    /// registration and disposal are lock-free and iteration snapshots a stable view.
    /// </summary>
    private readonly ConcurrentDictionary<Type, ConcurrentDictionary<Subscription, EventHandlerDelegate>> _subscriptions = new();

    /// <summary>Guards the availability flag together with the ordered retention buffer.</summary>
    private readonly object _retentionGate = new();

    /// <summary>Events published while degraded, retained in publish order for later delivery (Req 27.5).</summary>
    private readonly Queue<IDomainEvent> _retained = new();

    private bool _isAvailable = true;

    /// <summary>A type-erased handler invocation for a single subscribed delegate.</summary>
    private delegate Task EventHandlerDelegate(IDomainEvent @event, CancellationToken ct);

    /// <inheritdoc />
    public bool IsAvailable
    {
        get
        {
            lock (_retentionGate)
            {
                return _isAvailable;
            }
        }
    }

    /// <summary>
    /// The number of events currently held in the degraded-mode retention buffer awaiting delivery.
    /// Exposed for diagnostics and tests; zero whenever the bus is available and fully drained.
    /// </summary>
    public int RetainedEventCount
    {
        get
        {
            lock (_retentionGate)
            {
                return _retained.Count;
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var bucket = _subscriptions.GetOrAdd(typeof(TEvent), static _ => new ConcurrentDictionary<Subscription, EventHandlerDelegate>());

        EventHandlerDelegate dispatch = (@event, ct) => handler((TEvent)@event, ct);

        // The token is its own bucket key; disposal removes exactly this registration. The removal
        // closure is resolved lazily against the token so it is valid regardless of add/dispose order.
        var subscription = new Subscription(self => bucket.TryRemove(self, out _));
        bucket[subscription] = dispatch;

        return subscription;
    }

    /// <inheritdoc />
    public async Task PublishAsync(IDomainEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ct.ThrowIfCancellationRequested();

        // If degraded, retain in order and return without dispatching (Req 27.5).
        lock (_retentionGate)
        {
            if (!_isAvailable)
            {
                _retained.Enqueue(@event);
                return;
            }
        }

        await DispatchAsync(@event, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the bus availability. Transitioning to unavailable begins retaining published events; a
    /// value of <see langword="true"/> begins delivering again (Req 27.5). Prefer <see cref="RecoverAsync"/>
    /// when transitioning back to available so retained events are drained in order.
    /// </summary>
    public void SetAvailable(bool available)
    {
        lock (_retentionGate)
        {
            _isAvailable = available;
        }
    }

    /// <summary>
    /// Marks the bus available again and drains every retained event in its original publish order,
    /// dispatching each to current subscribers before returning (Req 27.5). Newly published events
    /// observed after recovery are delivered normally by <see cref="PublishAsync"/>.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct = default)
    {
        while (true)
        {
            IDomainEvent next;
            lock (_retentionGate)
            {
                _isAvailable = true;
                if (_retained.Count == 0)
                {
                    return;
                }

                next = _retained.Dequeue();
            }

            // Dispatch outside the retention lock so handlers cannot deadlock a concurrent publisher.
            await DispatchAsync(next, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Delivers <paramref name="event"/> to every handler subscribed to a type the event is
    /// assignable to (its own type plus any base types / interfaces subscribers registered for). The
    /// subscriber set is snapshotted before invocation so a handler that subscribes or unsubscribes
    /// during dispatch does not disturb the in-flight delivery.
    /// </summary>
    private async Task DispatchAsync(IDomainEvent @event, CancellationToken ct)
    {
        var eventType = @event.GetType();

        foreach (var (subscribedType, bucket) in _subscriptions)
        {
            if (!subscribedType.IsAssignableFrom(eventType))
            {
                continue;
            }

            // ConcurrentDictionary.Values snapshots, so disposals mid-dispatch are safe.
            foreach (var handler in bucket.Values)
            {
                ct.ThrowIfCancellationRequested();
                await handler(@event, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A live subscription handle. Disposal removes the underlying handler from its bucket exactly
    /// once so further published events are no longer delivered to it.
    /// </summary>
    private sealed class Subscription : IDisposable
    {
        private Action<Subscription>? _unsubscribe;

        public Subscription(Action<Subscription> unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
            unsubscribe?.Invoke(this);
        }
    }
}
