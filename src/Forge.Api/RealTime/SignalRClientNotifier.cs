using Forge.Api.Hubs;
using Forge.Infrastructure.RealTime;

using Microsoft.AspNetCore.SignalR;

namespace Forge.Api.RealTime;

/// <summary>
/// The Api-layer adapter that satisfies the <c>Real_Time_Channel</c> seam
/// <see cref="ISimulationClientNotifier"/> (task 33.1; Req 23.2, 23.4, 20.9). It bridges the
/// Infrastructure publisher — which must not reference the concrete hub type — to the SignalR
/// <see cref="SimulationHub"/> by forwarding each push to <c>IHubContext&lt;SimulationHub&gt;</c>.
/// <para>
/// Each <see cref="NotifyAsync"/> delivers a single client message named <c>eventName</c> carrying
/// <c>payload</c> to every connected client via <see cref="IClientProxy.SendAsync(string, object?, CancellationToken)"/>.
/// It does not block and makes no assumption about the calling thread (it is invoked from the
/// publisher's background pump). A genuinely unavailable channel surfaces as a faulted task from
/// <c>SendAsync</c>, which the publisher's pump swallows so the simulation keeps advancing (Req 23.5).
/// </para>
/// </summary>
public sealed class SignalRClientNotifier : ISimulationClientNotifier
{
    private readonly IHubContext<SimulationHub> _hubContext;

    /// <summary>
    /// Construct the notifier over the hub context used to reach all connected clients.
    /// </summary>
    /// <param name="hubContext">The SignalR context for <see cref="SimulationHub"/>.</param>
    public SignalRClientNotifier(IHubContext<SimulationHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    /// <inheritdoc />
    public Task NotifyAsync(string eventName, object payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        // Forward as a single named message to every client. We return the task directly (no await) so
        // the call never blocks the caller and any delivery fault flows back to the publisher's pump.
        return _hubContext.Clients.All.SendAsync(eventName, payload, ct);
    }
}
