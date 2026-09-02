namespace Forge.Application.Abstractions;

/// <summary>
/// The core's window to the world (design "The Input Driver seam"; Req 1.4, 2.6). A driver's sole
/// job is to feed the WMS Core the inputs it would otherwise receive from the real world — inbound
/// gel arrivals, colony demand/orders, temperature readings, and clock advancement — by issuing
/// commands through <see cref="IWarehouseCommandGateway"/>. The core processes those inputs through
/// its command/event use-case handlers and knows nothing about how they were produced.
/// <para>
/// This interface exposes lifecycle only. In Phase 1 the <c>Forge.Simulation</c> driver implements
/// it (the accelerated tick loop); a future real-world driver could implement it against real
/// sensors/scanners/ERP feeds without any change to the core. The Api composition root starts the
/// selected driver.
/// </para>
/// </summary>
public interface IWarehouseInputDriver
{
    /// <summary>Start feeding the core. Invoked by the Api composition root on startup.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Stop feeding the core and release any driver resources.</summary>
    Task StopAsync(CancellationToken ct = default);
}
