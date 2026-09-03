using Forge.Application.Abstractions;
using Forge.Contracts.Dtos;

using Microsoft.AspNetCore.SignalR;

namespace Forge.Api.Hubs;

/// <summary>
/// The <c>Real_Time_Channel</c> SignalR hub (task 33.1; Req 23.1, 23.2, 23.3, 23.4, 20.9; design
/// "Real-Time / SignalR Design"). It is the connection endpoint the Next.js / MAUI clients attach to.
/// <para>
/// <b>On connect (Req 23.3).</b> <see cref="OnConnectedAsync"/> reads the current
/// <see cref="SimulationSnapshotDto"/> from the core via
/// <see cref="IWarehouseCommandGateway.GetSnapshotAsync"/> and sends it to just the connecting client
/// as the initial full-state message named <see cref="SnapshotMethod"/>. The read is non-mutating.
/// </para>
/// <para>
/// <b>Incremental updates (Req 23.2, 23.4).</b> Per-event state changes are not pushed from the hub
/// itself; they are delivered by <c>Forge.Infrastructure.RealTime.SignalRStatePublisher</c> through the
/// <c>ISimulationClientNotifier</c> seam, whose Api implementation
/// (<see cref="RealTime.SignalRClientNotifier"/>) forwards to this hub's clients. Operator-parameter
/// changes flow the same way as an <c>OperatorParameterChanged</c> message so every client converges
/// (Req 20.9). This keeps the hub free of business rules — it only serves the initial snapshot.
/// </para>
/// </summary>
public sealed class SimulationHub : Hub
{
    /// <summary>
    /// The stable client-method name carrying the full <see cref="SimulationSnapshotDto"/> sent to a
    /// client on connect (Req 23.3). Clients subscribe to this to seed their initial state; kept as a
    /// named constant so incremental-update transports (REST/other clients) can match it.
    /// </summary>
    public const string SnapshotMethod = "Snapshot";

    private readonly IWarehouseCommandGateway _gateway;

    /// <summary>
    /// Construct the hub over the core command/query gateway used to read the initial snapshot.
    /// </summary>
    /// <param name="gateway">The WMS Core entrypoint whose read-only snapshot query seeds new clients.</param>
    public SimulationHub(IWarehouseCommandGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    /// <summary>
    /// On a new client connection, read the current simulation state and send it to the connecting
    /// client as the initial snapshot (Req 23.3). Uses <see cref="HubCallerContext.ConnectionAborted"/>
    /// so the read is cancelled if the client disconnects mid-fetch.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var snapshot = await _gateway.GetSnapshotAsync(Context.ConnectionAborted).ConfigureAwait(false);
        await Clients.Caller.SendAsync(SnapshotMethod, snapshot, Context.ConnectionAborted).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }
}
