using System.Threading.Channels;

using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Contracts.Events;
using Forge.Domain.Events;

namespace Forge.Infrastructure.RealTime;

/// <summary>
/// The Real_Time_Channel publisher (Infrastructure task 32.1; Req 20.9, 23.2, 23.4, 23.5; design
/// "Real-Time / SignalR Design"). It subscribes to the Application <see cref="IEventBus"/> and, for
/// domain events that change inventory / order / task / starship / operator-parameter state, maps
/// each to the corresponding <c>Forge.Contracts.Events</c> schema (or, for a parameter change, the
/// already-projected <see cref="Forge.Contracts.Dtos.OperatorParameterStateDto"/> wrapped in
/// <see cref="OperatorParameterChangedEvent"/>) and pushes it to connected clients through the
/// <see cref="ISimulationClientNotifier"/> seam.
///
/// <para><b>Never blocks the tick loop (Req 23.5, critical).</b> The event-bus handler runs on the
/// publishing thread â€” in Phase 1 the in-process bus dispatches synchronously on whoever called
/// <c>PublishAsync</c>, i.e. potentially the driver's tick loop. So the handler does the smallest
/// possible work: it maps the event to a DTO and performs a non-blocking <see cref="ChannelWriter{T}.TryWrite"/>
/// onto a bounded channel, then returns a completed task immediately. It never awaits SignalR
/// delivery. A background pump task drains the channel and performs the actual (awaited) push on its
/// own thread, fully decoupled from the bus.</para>
///
/// <para><b>Drops or queues while the channel is unavailable (Req 23.5).</b> The bounded channel is
/// created with <see cref="BoundedChannelFullMode.DropOldest"/>: pushes queue up to the capacity and,
/// once full (e.g. because SignalR is slow or unavailable and the pump is stalled), the oldest queued
/// push is discarded so newer state wins and the writer always succeeds without blocking. If a push
/// throws while being delivered (channel genuinely unavailable), the pump swallows the error and
/// moves on. Either way the simulation keeps advancing â€” the publisher never propagates an error back
/// into the bus and never stalls the publishing thread.</para>
///
/// <para><b>Lifecycle.</b> Construction subscribes to the bus and starts the pump. Disposal
/// unsubscribes, completes the channel, and awaits the pump's drain-and-exit. Disposal is idempotent.</para>
/// </summary>
public sealed class SignalRStatePublisher : IAsyncDisposable, IDisposable
{
    /// <summary>Default number of pending pushes retained before the oldest is dropped (Req 23.5).</summary>
    public const int DefaultCapacity = 1024;

    private readonly ISimulationClientNotifier _notifier;
    private readonly Channel<PendingPush> _channel;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private int _disposed;

    /// <summary>A single mapped update awaiting delivery: the client-method name and its payload DTO.</summary>
    private readonly record struct PendingPush(string EventName, object Payload);

    /// <summary>
    /// Subscribe to <paramref name="eventBus"/> for the state-changing events and start the background
    /// delivery pump that pushes mapped Contracts DTOs through <paramref name="notifier"/>.
    /// </summary>
    /// <param name="eventBus">The Application event bus to subscribe to (Req 23.2).</param>
    /// <param name="notifier">The Real_Time_Channel seam client pushes are delivered through (Req 23.4).</param>
    /// <param name="capacity">Max pending pushes before the oldest is dropped; defaults to <see cref="DefaultCapacity"/>.</param>
    public SignalRStatePublisher(IEventBus eventBus, ISimulationClientNotifier notifier, int? capacity = null)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(notifier);
        var cap = capacity ?? DefaultCapacity;
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        _notifier = notifier;

        // DropOldest + single-reader/multi-writer: writers (the bus dispatch thread) never block, and a
        // stalled/unavailable channel simply sheds the oldest queued state instead of back-pressuring
        // the tick loop (Req 23.5).
        _channel = Channel.CreateBounded<PendingPush>(new BoundedChannelOptions(cap)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        SubscribeAll(eventBus);

        _pump = Task.Run(() => PumpAsync(_shutdown.Token));
    }

    /// <summary>
    /// Subscribe a non-blocking handler for each state-changing event kind. Each handler maps the event
    /// to its Contracts payload and enqueues it, returning immediately (Req 23.2, 23.5).
    /// </summary>
    private void SubscribeAll(IEventBus eventBus)
    {
        // Inventory / cold-chain state.
        Subscribe<LotExpired>(eventBus, e => ("LotExpired", new LotExpiredEvent(e.LotId.Value, e.At)));
        Subscribe<TemperatureExcursion>(eventBus, e =>
            ("TemperatureExcursion", new TemperatureExcursionEvent(e.LotId.Value, e.Celsius, e.At)));
        Subscribe<BlockedArrival>(eventBus, e => ("BlockedArrival", new BlockedArrivalEvent(e.LotId.Value, e.Reason)));
        Subscribe<BlockedPlacement>(eventBus, e => ("BlockedPlacement", new BlockedPlacementEvent(e.LotId.Value, e.Reason)));

        // Task / routing state.
        Subscribe<UnroutableTask>(eventBus, e =>
            ("UnroutableTask", new UnroutableTaskEvent(e.TaskId.Value, e.OriginX, e.OriginY, e.DestinationX, e.DestinationY)));

        // Starship / dock / loading state.
        Subscribe<DockBlocked>(eventBus, e => ("DockBlocked", new DockBlockedEvent(e.DockBayId.Value, e.At)));
        Subscribe<LoadingWindowClosed>(eventBus, e =>
            ("LoadingWindowClosed", new LoadingWindowClosedEvent(e.StarshipId.Value, e.Loaded, e.Shortfall)));

        // Order / backlog state.
        Subscribe<BacklogChanged>(eventBus, e => ("BacklogChanged", new BacklogChangedEvent(e.Kind, e.NewSize)));

        // Operator-parameter state (Req 20.9). The Application event already carries the Contracts DTO,
        // so mapping is a direct wrap into the transport event.
        Subscribe<OperatorParameterChanged>(eventBus, e =>
            ("OperatorParameterChanged", new OperatorParameterChangedEvent(e.State)));
    }

    /// <summary>
    /// Register a bus subscription whose handler maps <typeparamref name="TEvent"/> to a
    /// (eventName, payload) pair and enqueues it without blocking (Req 23.5).
    /// </summary>
    private void Subscribe<TEvent>(IEventBus eventBus, Func<TEvent, (string EventName, object Payload)> map)
        where TEvent : IDomainEvent
    {
        _subscriptions.Add(eventBus.Subscribe<TEvent>((@event, _) =>
        {
            var (eventName, payload) = map(@event);
            Enqueue(eventName, payload);
            // Return a completed task: the bus (and thus the tick loop) never awaits SignalR delivery.
            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Non-blocking hand-off of a mapped push. On a full channel <see cref="BoundedChannelFullMode.DropOldest"/>
    /// discards the oldest pending push so this write always succeeds promptly (Req 23.5).
    /// </summary>
    private void Enqueue(string eventName, object payload)
    {
        // TryWrite returns false only once the channel is completed (during disposal); a full channel is
        // handled by DropOldest, not by failing the write. Either way we never block.
        _channel.Writer.TryWrite(new PendingPush(eventName, payload));
    }

    /// <summary>
    /// The background pump: drains queued pushes and delivers each through the notifier, swallowing any
    /// per-push failure so an unavailable channel never stops the pump or the simulation (Req 23.5).
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var push in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _notifier.NotifyAsync(push.EventName, push.Payload, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutting down; stop pumping.
                    return;
                }
                catch
                {
                    // Channel unavailable / delivery failed: drop this push and keep going (Req 23.5).
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the pump observes the shutdown token.
        }

        _shutdown.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Synchronous disposal path (e.g. a non-async ServiceProvider.Dispose()). We must NOT block the
        // disposing thread waiting for the pump task to drain: doing so risks a sync-over-async stall
        // when the pump's continuation is scheduled back onto a captured context. Instead we signal the
        // pump to stop (complete the channel + cancel the token) and give it a short, BOUNDED grace
        // period to observe the signal and exit. The pump is cooperative and daemon-like, so if it does
        // not finish within the grace window we proceed with disposal anyway; leaving it to unwind in the
        // background never blocks the caller (Req 23.5 — the publisher never stalls the rest of the system).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        // Bounded, non-throwing wait — never an unbounded block. A short window is ample for the pump to
        // observe channel-completion/cancellation and return.
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The pump surfaces only OperationCanceledException on shutdown, which Wait wraps in an
            // AggregateException; ignore it and any other teardown fault — disposal must not throw.
        }

        _shutdown.Dispose();
    }
}
