using Forge.Application.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Forge.Simulation;

/// <summary>
/// The Phase-1 <see cref="IWarehouseInputDriver"/> implementation (design "The Input Driver seam";
/// Req 1.4, 2.6). It is the core's window to the world: its sole job is to feed the WMS Core the
/// inputs it would otherwise receive from the real world (inbound arrivals, colony demand,
/// temperature readings, and clock advancement). It composes the four Simulation generators via the
/// <see cref="SimulationHostedService"/> tick loop, which does the per-tick generation and rule
/// application.
/// <para>
/// This type exposes lifecycle only (per <see cref="IWarehouseInputDriver"/>): its
/// <see cref="StartAsync"/>/<see cref="StopAsync"/> start and stop the tick loop by delegating to the
/// hosted service's <see cref="IHostedService"/> lifecycle. The Api composition root can either start
/// the driver explicitly through this abstraction, or let the generic host start the registered
/// <see cref="SimulationHostedService"/> directly — both paths drive the same loop. Keeping the driver
/// as a thin lifecycle wrapper means the WMS Core depends only on <see cref="IWarehouseInputDriver"/>
/// and could swap in a real-world driver without change (Req 2.6).
/// </para>
/// </summary>
public sealed class SimulationInputDriver : IWarehouseInputDriver
{
    private readonly SimulationHostedService _host;

    /// <summary>Create the driver over the tick-loop hosted service it starts and stops.</summary>
    /// <param name="host">The tick loop composing the accelerated clock and the four generators.</param>
    public SimulationInputDriver(SimulationHostedService host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>
    /// Pause simulated-time advancement (Req 10.5). While paused the tick loop keeps running but every
    /// tick applies zero simulated delta, so no inputs are generated and no rules are applied.
    /// </summary>
    public void Pause() => _host.Pause();

    /// <summary>Resume simulated-time advancement after a <see cref="Pause"/>.</summary>
    public void Resume() => _host.Resume();

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default) => _host.StartAsync(ct);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default) => _host.StopAsync(ct);
}
