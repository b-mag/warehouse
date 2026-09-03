namespace Forge.Infrastructure.RealTime;

/// <summary>
/// The Real_Time_Channel seam the <see cref="SignalRStatePublisher"/> pushes mapped Contracts DTOs
/// through (Req 23.1, 23.2, 23.4; design "Real-Time / SignalR Design").
/// <para>
/// <b>Why a seam and not <c>IHubContext&lt;THub&gt;</c> directly.</b> The concrete SignalR hub
/// (<c>SimulationHub</c>) lives in the Api layer (task 33.1), which the Infrastructure layer must not
/// reference. Rather than have Infrastructure take a generic
/// <c>Microsoft.AspNetCore.SignalR.IHubContext&lt;THub&gt;</c> parameterised on a hub type it cannot
/// see, the publisher depends on this small abstraction. The Api's hub context adapts to it: task
/// 33.1 registers an implementation that forwards each <see cref="NotifyAsync"/> call to
/// <c>IHubContext&lt;SimulationHub&gt;.Clients.All.SendAsync(eventName, payload, ct)</c>.
/// </para>
/// <para>
/// <b>Contract the Api (task 33.1) must satisfy.</b> An implementation SHALL, for a given
/// <paramref name="eventName"/> and Contracts DTO <paramref name="payload"/>, deliver a single SignalR
/// client message named <paramref name="eventName"/> carrying <paramref name="payload"/> to every
/// connected client, and SHALL surface a genuinely unavailable channel by faulting the returned task
/// (which the publisher's background pump swallows so the simulation keeps advancing — Req 23.5). The
/// implementation MUST NOT assume it is called from any particular thread and MUST NOT block: it is
/// invoked from the publisher's background pump, never from the tick loop.
/// </para>
/// </summary>
public interface ISimulationClientNotifier
{
    /// <summary>
    /// Push a single named real-time message carrying <paramref name="payload"/> (a Contracts DTO /
    /// Contracts event record) to all connected clients (Req 23.2, 23.4).
    /// </summary>
    /// <param name="eventName">The SignalR client-method name identifying the update kind.</param>
    /// <param name="payload">The Contracts DTO / event schema describing the updated state.</param>
    /// <param name="ct">A token cancelled when the publisher is shutting down.</param>
    Task NotifyAsync(string eventName, object payload, CancellationToken ct = default);
}
