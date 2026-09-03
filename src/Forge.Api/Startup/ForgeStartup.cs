using Forge.Api.Simulation;
using Forge.Application.Abstractions;
using Forge.Application.Docks;
using Forge.Domain.Docks;
using Forge.Infrastructure;
using Forge.Infrastructure.Adapters;
using Forge.Infrastructure.Persistence;
using Forge.Infrastructure.RealTime;
using Forge.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Api.Startup;

/// <summary>
/// The headless startup routine for the WMS Core + Simulation driver (task 33.3; Req 26.2, 26.3, 26.5,
/// 10.1). Run once by <c>Program</c> before the host starts serving: it brings up the database, applies
/// migrations, seeds an empty database, installs the live demo spatial state and dock bays, and primes
/// the Simulation catalog — so that once the host starts, the registered
/// <c>SimulationHostedService</c> tick loop immediately drives a live, seeded warehouse (arrivals,
/// demand, temperature readings, tick rules) with no Game required.
/// </summary>
public static class ForgeStartup
{
    /// <summary>
    /// Initialize the engine before the host runs (task 33.3):
    /// <list type="number">
    ///   <item><b>Bring up the database (Req 26.1, 26.5).</b> Start the <see cref="IEmbeddedDatabaseHost"/>;
    ///     a failure fails startup with a descriptive error.</item>
    ///   <item><b>Apply migrations (Req 26.2).</b> <c>Database.MigrateAsync</c> against the store; a
    ///     failure (e.g. Postgres unreachable) fails startup with a descriptive error (Req 26.5).</item>
    ///   <item><b>Seed when empty (Req 26.3).</b> If no gel types exist, run the
    ///     <see cref="WarehouseSeeder"/>; a seed failure fails startup with a descriptive error.</item>
    ///   <item><b>Install the live world.</b> Register the synthesized dock bays with the
    ///     <see cref="DockScheduler"/> and install the demo spatial state (grid + agents + starships)
    ///     into the tick-state provider, keyed to the seeded colonies + the clock's current time.</item>
    ///   <item><b>Prime the Simulation catalog.</b> Refresh the seeded catalog so the first ticks have
    ///     gel types / colonies / lots to generate against, and force-resolve the SignalR state
    ///     publisher so it subscribes to the event bus before any event flows.</item>
    /// </list>
    /// </summary>
    /// <param name="services">The built application's root service provider.</param>
    /// <param name="ct">A cancellation token for the async startup work.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a descriptive message when the database cannot be initialized, migrated, or seeded
    /// (Req 26.5). The message names the failing step and carries the underlying cause.
    /// </exception>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.GetRequiredService<ForgeInfrastructureOptions>();

        // 1) Bring up / attach to the database (Req 26.1, 26.5).
        var dbHost = services.GetRequiredService<IEmbeddedDatabaseHost>();
        try
        {
            await dbHost.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Forge startup failed: the backing Postgres database could not be initialized. " +
                "Set the 'Forge:ConnectionString' configuration value (or the ConnectionStrings:Forge " +
                "connection string) to point at a reachable Postgres instance. See the inner exception " +
                "for details.",
                ex);
        }

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        // 2) Apply EF Core migrations (Req 26.2). Fail fast with a descriptive error (Req 26.5).
        try
        {
            await context.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Forge startup failed: EF Core migrations could not be applied to the database at the " +
                "configured connection string. Ensure Postgres is running and reachable (Req 26.2, 26.5). " +
                "See the inner exception for details.",
                ex);
        }

        // 3) Seed when the database is empty (Req 26.3).
        var alreadySeeded = await context.GelTypes.AnyAsync(ct).ConfigureAwait(false);
        if (!alreadySeeded)
        {
            var seeder = scope.ServiceProvider.GetRequiredService<WarehouseSeeder>();
            var seedResult = await seeder
                .SeedAsync(new WarehouseSeedOptions(Seed: options.Seed), ct)
                .ConfigureAwait(false);

            if (seedResult.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Forge startup failed: seeding the empty database did not complete (Req 26.3): " +
                    $"{seedResult.Error.Message}");
            }
        }

        // 4) Install the live demo world: register dock bays with the scheduler + install spatial state.
        var clock = services.GetRequiredService<IClock>();
        var now = clock.Now;

        RegisterDockBays(services, options);
        await InstallSpatialStateAsync(services, scope.ServiceProvider, options, now, ct).ConfigureAwait(false);

        // 5) Prime the Simulation catalog so the first ticks have something to generate against, and
        //    force-resolve the SignalR publisher so it is subscribed to the event bus before events flow.
        await services.GetRequiredService<SeededSimulationCatalogProvider>()
            .RefreshAsync(ct)
            .ConfigureAwait(false);

        _ = services.GetRequiredService<SignalRStatePublisher>();
    }

    // Register the synthesized dock bays (open) with the dock scheduler so inbound receipts can be
    // assigned a bay slot; without registered bays every arrival would be blocked (Req 17.2, 14.6).
    private static void RegisterDockBays(IServiceProvider services, ForgeInfrastructureOptions options)
    {
        var scheduler = services.GetRequiredService<DockScheduler>();
        var dockBays = ForgeInfrastructureDependencyInjection.SynthesizeDockBays(options.Seed, options.DockBayCount);

        foreach (var bayId in dockBays)
        {
            scheduler.RegisterBay(new DockBay(bayId, isOpen: true));
        }
    }

    // Install the demo spatial state (grid + agents + starships) keyed to the seeded colonies so the
    // movement and starship-loading stages run and the snapshot renders moving agents (Req 18, 13).
    private static async Task InstallSpatialStateAsync(
        IServiceProvider services,
        IServiceProvider scoped,
        ForgeInfrastructureOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var colonies = await scoped
            .GetRequiredService<Application.Abstractions.Repositories.IColonyRepository>()
            .ListAllAsync(ct)
            .ConfigureAwait(false);

        var destinations = colonies.Select(c => c.Id).OrderBy(id => id).ToArray();

        services.GetRequiredService<InMemoryTickStateProvider>()
            .Initialize(options.Seed, now, destinations);
    }
}
