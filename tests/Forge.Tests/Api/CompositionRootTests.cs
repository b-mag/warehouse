using Forge.Api.Controllers;
using Forge.Api.Forecasting;
using Forge.Api.RealTime;
using Forge.Api.Startup;
using Forge.Application;
using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Inbound;
using Forge.Application.OperatorParameters;
using Forge.Application.Simulation;
using Forge.Domain.Common;
using Forge.Infrastructure;
using Forge.Infrastructure.Persistence;
using Forge.Infrastructure.RealTime;
using Forge.Simulation;
using Forge.Simulation.Clock;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Forge.Tests.Api;

/// <summary>
/// Composition-root DI-validation tests for the headless Api (task 33.3). These are the safety net for
/// the most common wiring bug: a singleton (the tick loop / gateway) capturing a scoped
/// <see cref="ForgeDbContext"/>. They rebuild the exact service graph the <c>Program</c> composition
/// root wires — <see cref="DependencyInjection.AddForgeApplication"/>,
/// <see cref="ForgeInfrastructureDependencyInjection.AddForgeInfrastructure"/>,
/// <see cref="Forge.Simulation.DependencyInjection.AddSimulationDriver"/>, plus the Api-owned seams
/// (catalog provider, SignalR notifier, forecast store) — then build the provider with
/// <c>ValidateScopes: true</c> and assert every key service and each controller resolves.
/// <para>
/// <b>No live Postgres.</b> The Npgsql registration is kept exactly as production wires it — resolving
/// the <see cref="ForgeDbContext"/> never opens a connection, so the whole scoped persistence graph
/// validates against a dummy connection string. The single production dependency the DI graph has on
/// <i>data</i> at construction time is the <c>ArrivalGenerator</c>, which reads the gel-type catalog
/// eagerly; in production <see cref="ForgeStartup.InitializeAsync"/> primes that catalog from the seeded
/// database before the host resolves the driver. To reproduce that ordering DB-free, these tests
/// substitute a tiny in-test <see cref="ISimulationCatalogProvider"/> that returns one gel type + the
/// synthesized dock bays (the concrete <c>SeededSimulationCatalogProvider</c> is covered by its own
/// tests). Everything else — gateway, clock, driver, hosted service, adapters, repositories,
/// controllers — is the real production registration.
/// </para>
/// </summary>
public sealed class CompositionRootTests
{
    // A syntactically valid but never-connected connection string. Resolving the DbContext does not open
    // a connection (Npgsql connects lazily on first query, which these tests never trigger).
    private const string DummyConnectionString =
        "Host=localhost;Port=5432;Database=forge_ditest;Username=forge;Password=forge";

    // Build the same service collection the Program composition root builds (task 33.3), with scope
    // validation on so a singleton capturing a scoped dependency fails on resolve.
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        // Logging (the CatalogRefreshService takes an ILogger<T>; Program gets it from the host builder).
        services.AddLogging();

        var infrastructureOptions = new ForgeInfrastructureOptions
        {
            ConnectionString = DummyConnectionString,
        };
        var operatorOptions = new OperatorParameterOptions
        {
            WorkerMax = 25,
            ModeledDockBays = infrastructureOptions.DockBayCount,
        };

        services.AddForgeApplication(operatorOptions);
        services.AddForgeInfrastructure(infrastructureOptions);
        services.AddSimulationDriver(options =>
        {
            options.InitialArrivalRatePerHour = 12.0;
            options.DemandMultiplier = 1.0;
        });

        // Api-owned seams (mirrors Program) — except the catalog provider, which we substitute with an
        // already-primed in-test one so the ArrivalGenerator's eager gel-type read succeeds DB-free
        // (production primes the real provider from the seeded DB in ForgeStartup before this resolves).
        var dockBays = ForgeInfrastructureDependencyInjection.SynthesizeDockBays(
            infrastructureOptions.Seed, infrastructureOptions.DockBayCount);
        services.AddSingleton<ISimulationCatalogProvider>(new PrimedCatalog(dockBays));
        services.AddSingleton<ISimulationClientNotifier, SignalRClientNotifier>();
        services.AddSingleton<IForecastReviewStore, InMemoryForecastReviewStore>();

        // The transport layer needs the SignalR + MVC services the notifier / hub depend on (the
        // SignalRClientNotifier takes an IHubContext<SimulationHub>).
        services.AddSignalR();
        services.AddControllers();

        // ValidateScopes catches the classic singleton-captures-scoped-DbContext bug on every resolve —
        // the correctness point this whole test class guards. We deliberately do NOT set
        // ValidateOnBuild: it eagerly validates every descriptor including the ASP.NET Core framework
        // internals (MVC routing, the SignalR connection manager) whose full graph only materializes
        // under a real WebApplication host (they need IHostApplicationLifetime / IWebHostEnvironment).
        // Those are framework registrations, not Forge wiring; this test targets the Forge service graph,
        // which it validates by resolving each Forge service below.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
    }

    [Fact]
    public void Composition_root_builds_the_service_graph()
    {
        using var provider = BuildProvider();
        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData(typeof(IWarehouseCommandGateway))]
    [InlineData(typeof(IClock))]
    [InlineData(typeof(ISimulationCatalogProvider))]
    [InlineData(typeof(ITickStateProvider))]
    [InlineData(typeof(IPathPlanner))]
    [InlineData(typeof(IReservationManager))]
    [InlineData(typeof(IMLPredictor))]
    [InlineData(typeof(IEventBus))]
    [InlineData(typeof(IWarehouseInputDriver))]
    [InlineData(typeof(IForecastAuditSink))]
    [InlineData(typeof(SimulationHostedService))]
    [InlineData(typeof(SignalRStatePublisher))]
    [InlineData(typeof(SimulationClock))]
    public void Key_singleton_services_resolve(Type serviceType)
    {
        using var provider = BuildProvider();

        var service = provider.GetService(serviceType);

        Assert.NotNull(service);
    }

    [Fact]
    public void The_gel_type_catalog_resolves_within_a_scope()
    {
        // IGelTypeCatalog is scoped (it reads the scoped DbContext), so it resolves from a scope, not root.
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<IGelTypeCatalog>());
    }

    [Fact]
    public void Simulation_hosted_service_is_registered_as_an_IHostedService()
    {
        using var provider = BuildProvider();

        var hostedServices = provider.GetServices<IHostedService>();

        Assert.Contains(hostedServices, s => s is SimulationHostedService);
    }

    [Fact]
    public void The_accelerated_clock_is_the_cores_IClock_instance()
    {
        using var provider = BuildProvider();

        // The core depends only on IClock; it must be the same accelerated SimulationClock instance so
        // operator pause/resume/configure flows to the tick loop (Req 10.6, 10.7).
        var clock = provider.GetRequiredService<IClock>();
        var simClock = provider.GetRequiredService<SimulationClock>();

        Assert.Same(simClock, clock);
    }

    [Theory]
    [InlineData(typeof(IGelLotRepository))]
    [InlineData(typeof(IZoneRepository))]
    [InlineData(typeof(IColonyRepository))]
    [InlineData(typeof(IOrderRepository))]
    [InlineData(typeof(ITaskRepository))]
    [InlineData(typeof(IWorkerRepository))]
    [InlineData(typeof(IUnitOfWork))]
    public void Scoped_persistence_services_resolve_within_a_scope(Type serviceType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    [Theory]
    [InlineData(typeof(OrdersController))]
    [InlineData(typeof(OperatorParametersController))]
    [InlineData(typeof(ForecastController))]
    [InlineData(typeof(QueryController))]
    public void Each_controller_can_be_activated_from_resolved_services(Type controllerType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        // MVC activates controllers via ActivatorUtilities (they are not container-registered), so this
        // validates each controller's constructor dependencies are all resolvable from the graph.
        var controller = ActivatorUtilities.CreateInstance(scope.ServiceProvider, controllerType);

        Assert.NotNull(controller);
    }

    // A minimal already-primed catalog standing in for the DB-backed SeededSimulationCatalogProvider so
    // the ArrivalGenerator's eager gel-type read succeeds without a live database, mirroring the primed
    // state ForgeStartup installs before the host resolves the driver. Returns one gel type, the real
    // synthesized dock bays, and no colonies / temperature targets (neither is read at construction).
    private sealed class PrimedCatalog : ISimulationCatalogProvider
    {
        public PrimedCatalog(IReadOnlyList<DockBayId> dockBays)
        {
            DockBays = dockBays;
            GelTypes = new[] { GelTypeId.New() };
            Colonies = Array.Empty<ColonyDemandSource>();
            TemperatureTargets = Array.Empty<TemperatureReadingTarget>();
        }

        public IReadOnlyList<GelTypeId> GelTypes { get; }
        public IReadOnlyList<DockBayId> DockBays { get; }
        public IReadOnlyList<ColonyDemandSource> Colonies { get; }
        public IReadOnlyList<TemperatureReadingTarget> TemperatureTargets { get; }
    }
}
