using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Docks;
using Forge.Application.Inbound;
using Forge.Application.OperatorParameters;
using Forge.Application.Queries;
using Forge.Application.Simulation;
using Forge.Domain.Common;
using Forge.Infrastructure.Adapters;
using Forge.Infrastructure.Gateway;
using Forge.Infrastructure.Messaging;
using Forge.Infrastructure.Ml;
using Forge.Infrastructure.Persistence;
using Forge.Infrastructure.Persistence.Repositories;
using Forge.Infrastructure.RealTime;
using Forge.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Infrastructure;

/// <summary>
/// Deployment configuration for <see cref="ForgeInfrastructureDependencyInjection.AddForgeInfrastructure"/>
/// (task 33.3). Carries the persistence knobs (connection string + embedded/container mode), the
/// deterministic seed threaded through synthesized ids, and the count of dock bays the arrival catalog
/// and dock scheduler are wired with.
/// </summary>
public sealed class ForgeInfrastructureOptions
{
    /// <summary>
    /// The Npgsql connection string the <see cref="ForgeDbContext"/> binds to (Req 26.1). The single
    /// knob a user sets to point the engine at their Postgres (embedded local instance or a
    /// container-hosted / external Postgres).
    /// </summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=forge;Username=forge;Password=forge";

    /// <summary>
    /// Whether the database host manages a local embedded Postgres instance (<c>true</c>, the default
    /// per Req 26.1) or attaches to an externally-provided connection string such as a container-hosted
    /// Postgres (<c>false</c>). Embedded is the default usable path: a <see cref="MysticMindPostgresProvisioner"/>
    /// brings up a self-managed local Postgres with no external setup. Setting this <c>false</c> switches
    /// to container mode (attach to <see cref="ConnectionString"/>); startup then fails fast with a
    /// descriptive error if the store is unreachable (Req 26.5).
    /// </summary>
    public bool Embedded { get; set; } = true;

    /// <summary>The deterministic seed for synthesized ids (dock bays, demo agents, starships).</summary>
    public int Seed { get; set; }

    /// <summary>How many dock bays to synthesize for the arrival catalog + dock scheduler (Req 17, 20.4).</summary>
    public int DockBayCount { get; set; } = 4;
}

/// <summary>
/// Composition-root wiring for the WMS Core <b>Infrastructure</b> layer (task 33.3). Registers the EF
/// Core persistence stack (the Npgsql-backed <see cref="ForgeDbContext"/>, the repositories, and the
/// unit of work), the in-process event bus, the statistical ML baseline, the concrete command gateway,
/// the Phase-2 seam adapters (path planner, reservation manager, forecast audit sink, tick-state
/// provider, gel-type catalog), the seeded Simulation catalog provider, the real-time SignalR state
/// publisher, and the database bootstrap host.
/// </summary>
public static class ForgeInfrastructureDependencyInjection
{
    /// <summary>
    /// Register the WMS Core Infrastructure services (task 33.3). Call after <c>AddForgeApplication</c>
    /// and before <c>AddSimulationDriver</c> (which registers the accelerated clock as <c>IClock</c>).
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="options">The persistence + seed + dock-bay configuration.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddForgeInfrastructure(
        this IServiceCollection services,
        ForgeInfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(options);

        // ---- Database bootstrap host (Req 26.1). Embedded mode is the default usable path; the
        //      MysticMindPostgresProvisioner brings up a self-managed local Postgres (no external
        //      setup). Container mode (Embedded=false) instead attaches to the configured string. ----
        services.TryAddSingleton(new EmbeddedDatabaseOptions
        {
            ConnectionString = options.ConnectionString,
            Embedded = options.Embedded,
        });

        // The embedded-Postgres provisioner. Registered unconditionally (TryAddSingleton) but only
        // actually resolved/started when Embedded=true; container mode never touches it.
        services.TryAddSingleton<IEmbeddedPostgresProvisioner, MysticMindPostgresProvisioner>();

        services.TryAddSingleton<IEmbeddedDatabaseHost>(sp =>
            new EmbeddedDatabaseHost(
                sp.GetRequiredService<EmbeddedDatabaseOptions>(),
                sp.GetService<IEmbeddedPostgresProvisioner>()));

        // ---- EF Core persistence (scoped). The Npgsql provider backs both embedded and container
        //      Postgres; only the connection string differs (Req 26.1). The DbContext binds to the
        //      *effective* connection string exposed by the started host — for embedded mode that is
        //      the provisioner-picked port (known only after StartAsync), for container mode it is the
        //      configured string. ForgeStartup.InitializeAsync starts the host BEFORE the first
        //      DbContext is resolved, so by then ConnectionString is valid.
        //
        //      Pre-start fallback (keeps DI validation working without a live server): tests such as
        //      CompositionRootTests build the provider and resolve the DbContext WITHOUT starting the
        //      host, so reading ConnectionString would throw. We fall back to the configured
        //      EmbeddedDatabaseOptions.ConnectionString (a syntactically valid string) so option-building
        //      never throws. Npgsql connects lazily, so no connection is opened until a real query runs
        //      (which only happens post-start in production). ----
        services.AddDbContext<ForgeDbContext>((sp, builder) =>
        {
            var host = sp.GetRequiredService<IEmbeddedDatabaseHost>();
            var dbOptions = sp.GetRequiredService<EmbeddedDatabaseOptions>();

            string connectionString;
            try
            {
                // Effective string once the host has started (embedded: provisioner port; container: configured).
                connectionString = host.ConnectionString;
            }
            catch (InvalidOperationException)
            {
                // Host not started yet (DI-validation path): fall back to the configured string so the
                // Npgsql options build without a live server. The real string is used post-start.
                connectionString = dbOptions.ConnectionString;
            }

            builder.UseNpgsql(connectionString);
        });

        // Repositories + unit of work share the scoped DbContext (scoped).
        services.TryAddScoped<IGelLotRepository, GelLotRepository>();
        services.TryAddScoped<IZoneRepository, ZoneRepository>();
        services.TryAddScoped<IColonyRepository, ColonyRepository>();
        services.TryAddScoped<IOrderRepository, OrderRepository>();
        services.TryAddScoped<ITaskRepository, TaskRepository>();
        services.TryAddScoped<IWorkerRepository, WorkerRepository>();
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();

        // The gel-type catalog reads the seeded gel types from the scoped context (scoped).
        services.TryAddScoped<IGelTypeCatalog, EfGelTypeCatalog>();

        // The snapshot handler needs a primary dock bay (Req 17.4) alongside the scoped repositories, so
        // it is registered here (Infrastructure knows the synthesized dock bays) rather than in the
        // Application wiring. Scoped, because it reads the scoped zone/lot repositories.
        var dockBays = SynthesizeDockBays(options.Seed, options.DockBayCount);
        var primaryDockBay = dockBays[0];
        services.TryAddScoped(sp => new GetSimulationSnapshotHandler(
            sp.GetRequiredService<IZoneRepository>(),
            sp.GetRequiredService<IGelLotRepository>(),
            sp.GetRequiredService<ITickStateProvider>(),
            sp.GetRequiredService<WarehouseMetrics>(),
            sp.GetRequiredService<DockScheduler>(),
            primaryDockBay,
            sp.GetRequiredService<OperatorParameterState>()));

        // ---- In-process event bus (singleton, degraded-mode capable — Req 27). ----
        services.TryAddSingleton<InProcessEventBus>();
        services.TryAddSingleton<IEventBus>(sp => sp.GetRequiredService<InProcessEventBus>());

        // ---- ML baseline (singleton, stateless — Req 21.1). ----
        services.TryAddSingleton<IMLPredictor, StatisticalBaselinePredictor>();

        // ---- Phase-2 seam adapters. ----
        services.TryAddSingleton<IPathPlanner, AStarPathPlannerAdapter>();     // stateless (Req 18.3)
        services.TryAddSingleton<IReservationManager, ReservationManager>();   // live ledger (Req 19)
        services.TryAddSingleton<IForecastAuditSink, LoggingForecastAuditSink>(); // Phase-1 sink (Req 22.5)
        services.TryAddSingleton<InMemoryTickStateProvider>();                 // live spatial state
        services.TryAddSingleton<ITickStateProvider>(sp =>
            sp.GetRequiredService<InMemoryTickStateProvider>());

        // ---- The concrete command gateway (singleton, opens a fresh scope per operation). ----
        services.TryAddSingleton<IWarehouseCommandGateway, WarehouseCommandGateway>();

        // NOTE: the Simulation catalog provider (ISimulationCatalogProvider) is registered by the Api
        // composition root, not here — its interface + catalog record types live in Forge.Simulation,
        // which Infrastructure deliberately does not reference (the layer boundary: Infrastructure ->
        // Application/Domain/Contracts only, task 1.1). The Api projects the seeded state (via the
        // IServiceScopeFactory + the synthesized dock bays exposed by SynthesizeDockBays) into it.

        // ---- Real-time SignalR state publisher (singleton). It subscribes to the event bus in its
        //      constructor, so it must be instantiated at startup; the Api registers the concrete
        //      ISimulationClientNotifier (its hub-context adapter) before building the provider. ----
        services.TryAddSingleton<SignalRStatePublisher>(sp => new SignalRStatePublisher(
            sp.GetRequiredService<IEventBus>(),
            sp.GetRequiredService<ISimulationClientNotifier>()));

        // ---- Seeder (scoped — it writes through the scoped DbContext). ----
        services.TryAddScoped(sp => new WarehouseSeeder(sp.GetRequiredService<ForgeDbContext>()));

        return services;
    }

    /// <summary>
    /// Synthesize a deterministic set of dock-bay ids (the seeder persists none in Phase 1). Shared by
    /// the arrival catalog and the dock scheduler so both reason about the same bays. Public so the Api
    /// composition root can build the same bays for the Simulation catalog provider it registers.
    /// </summary>
    public static IReadOnlyList<DockBayId> SynthesizeDockBays(int seed, int count)
    {
        var bays = new List<DockBayId>(Math.Max(1, count));
        var effective = count < 1 ? 1 : count;
        for (var i = 0; i < effective; i++)
        {
            bays.Add(new DockBayId(DeterministicIds.Derive(seed, "dockbay", i)));
        }

        bays.Sort();
        return bays;
    }
}
