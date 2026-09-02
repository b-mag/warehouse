using Forge.Domain.Events;

namespace Forge.Application.Abstractions;

/// <summary>
/// The messaging seam the WMS Core publishes domain events through (design "WMS Core Application
/// abstractions"; Req 27). In Phase 1 an in-process bus (in <c>Forge.Infrastructure</c>) implements
/// it; Phase 2 could swap in RabbitMQ/MassTransit without any change to the core.
/// </summary>
public interface IEventBus
{
    /// <summary>Publish a domain event to all subscribers (Req 27.3, 27.4).</summary>
    Task PublishAsync(IDomainEvent @event, CancellationToken ct = default);

    /// <summary>
    /// Subscribe a handler to events of type <typeparamref name="TEvent"/>. Returns an
    /// <see cref="IDisposable"/> whose disposal cancels the subscription.
    /// </summary>
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent;

    /// <summary>
    /// Whether the bus is currently available. When false, degraded-mode support retains events for
    /// later delivery (Req 27.5).
    /// </summary>
    bool IsAvailable { get; }
}
