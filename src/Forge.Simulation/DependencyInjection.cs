using Forge.Application.Abstractions;
using Forge.Simulation.Arrivals;
using Forge.Simulation.Clock;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Forge.Simulation;

/// <summary>
/// Composition-root wiring for the Phase-1 Simulation input driver (design "AddSimulationDriver(...)
/// wiring"; Req 10.1). <see cref="AddSimulationDriver"/> registers the accelerated
/// <see cref="SimulationClock"/> as the core's <see cref="IClock"/> (singleton), the four seeded
/// generators, the <see cref="SimulationHostedService"/> tick loop (also as the host's
/// <see cref="IHostedService"/>), and the <see cref="SimulationInputDriver"/> as the core's
/// <see cref="IWarehouseInputDriver"/>.
/// <para>
/// The caller must have already registered the WMS Core's <see cref="IWarehouseCommandGateway"/> and
/// must register an <see cref="ISimulationCatalogProvider"/> (the catalog seam) — that provider comes
/// from Infrastructure seeding / the Api composition root, which project live seeded warehouse state
/// into generator catalogs. This method does not register a catalog provider so it stays decoupled
/// from persistence.
/// </para>
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Register the Simulation driver: accelerated clock (as <see cref="IClock"/>), the four
    /// generators, the tick-loop hosted service, and the <see cref="IWarehouseInputDriver"/>.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="configure">
    /// Optional configuration of <see cref="SimulationDriverOptions"/> (seeds, initial operator
    /// parameters, loop cadence). When omitted, defaults are used.
    /// </param>
    /// <param name="clockStart">
    /// The initial simulated time the accelerated clock starts at. Defaults to
    /// <see cref="DateTimeOffset.UnixEpoch"/> so runs start from a fixed, reproducible instant.
    /// </param>
    /// <param name="clockMode">The clock's starting mode (defaults to accelerated).</param>
    /// <param name="accelerationFactor">
    /// The starting acceleration factor when <paramref name="clockMode"/> is
    /// <see cref="ClockMode.Accelerated"/> (must be &gt;= 1).
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSimulationDriver(
        this IServiceCollection services,
        Action<SimulationDriverOptions>? configure = null,
        DateTimeOffset? clockStart = null,
        ClockMode clockMode = ClockMode.Accelerated,
        double accelerationFactor = 60.0)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SimulationDriverOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        // The accelerated clock is a single shared instance: it IS the core's IClock and the loop's
        // clock, so operator pause/resume/configure on one is observed by the other (Req 10.5, 10.6, 10.7).
        var start = clockStart ?? DateTimeOffset.UnixEpoch;
        services.TryAddSingleton(_ => new SimulationClock(start, clockMode, accelerationFactor));
        // Expose the same instance under the core's abstraction. The core depends only on IClock and
        // never on the concrete SimulationClock (Req 10.6, 10.7).
        services.TryAddSingleton<IClock>(sp => sp.GetRequiredService<SimulationClock>());

        // The seeded generators. Each owns its own PRNG stream keyed off its option seed so concerns
        // stay independent and reproducible.
        services.TryAddSingleton(sp =>
        {
            var gateway = sp.GetRequiredService<IWarehouseCommandGateway>();
            var catalog = sp.GetRequiredService<ISimulationCatalogProvider>();
            var opts = sp.GetRequiredService<SimulationDriverOptions>();
            return new ArrivalGenerator(
                gateway,
                opts.ArrivalSeed,
                catalog.GelTypes,
                catalog.DockBays,
                initialArrivalRatePerHour: opts.InitialArrivalRatePerHour);
        });

        services.TryAddSingleton(sp => new ColonyDemandSimulator(
            sp.GetRequiredService<IWarehouseCommandGateway>(),
            sp.GetRequiredService<SimulationDriverOptions>().DemandSeed));

        services.TryAddSingleton(sp => new TemperatureReadingGenerator(
            sp.GetRequiredService<IWarehouseCommandGateway>(),
            sp.GetRequiredService<SimulationDriverOptions>().TemperatureSeed));

        // The tick loop. Registered as a concrete singleton (so the driver can wrap its lifecycle) and
        // as an IHostedService (so the generic host starts/stops it).
        services.TryAddSingleton<SimulationHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SimulationHostedService>());

        // The driver seam the WMS Core / Api composition root sees.
        services.TryAddSingleton<SimulationInputDriver>();
        services.TryAddSingleton<IWarehouseInputDriver>(sp => sp.GetRequiredService<SimulationInputDriver>());

        return services;
    }
}
