using Forge.Api.Forecasting;
using Forge.Api.Hubs;
using Forge.Api.RealTime;
using Forge.Api.Simulation;
using Forge.Api.Startup;
using Forge.Application;
using Forge.Application.OperatorParameters;
using Forge.Infrastructure;
using Forge.Infrastructure.RealTime;
using Forge.Simulation;
using Microsoft.Extensions.DependencyInjection;

// ============================================================================================
// Forge Api â€” headless composition root (task 33.3).
//
// This wires the simulation-agnostic WMS Core (Application + Infrastructure) together with the
// Phase-1 input driver (Forge.Simulation) and runs fully headless (no Game). Startup brings up the
// database, applies migrations, seeds an empty database, installs the live demo world, and then the
// registered Simulation tick loop (an IHostedService) drives the core with an accelerated clock so
// the warehouse comes alive â€” arrivals, colony demand, temperature readings, and per-tick rules â€”
// while REST controllers and the SignalR SimulationHub stream state to any client.
//
// The Simulation driver could be swapped for a real-world driver (issuing the same gateway commands)
// plus the Infrastructure WallClock with no change to the WMS Core (design "The Input Driver seam").
// ============================================================================================

var builder = WebApplication.CreateBuilder(args);

// ---- Configuration knobs ---------------------------------------------------------------------
// The single knob a user sets to run against their Postgres: the connection string. Resolution order:
//   1. ConnectionStrings:Forge (standard .NET connection-string section), else
//   2. Forge:ConnectionString (the Forge options section), else
//   3. the built-in local-loopback default (Host=localhost;Port=5432;Database=forge;...).
var infrastructureOptions = new ForgeInfrastructureOptions();
builder.Configuration.GetSection("Forge").Bind(infrastructureOptions);
var configuredConnection =
    builder.Configuration.GetConnectionString("Forge")
    ?? builder.Configuration["Forge:ConnectionString"];
if (!string.IsNullOrWhiteSpace(configuredConnection))
{
    infrastructureOptions.ConnectionString = configuredConnection;
}

// Operator-parameter bounds + initial values (Req 20). Modeled dock bays match the synthesized dock
// bays so the open-dock-bays parameter is bounded by the physically modeled bays (Req 20.4).
var operatorOptions = new OperatorParameterOptions
{
    WorkerMax = 25,
    ModeledDockBays = infrastructureOptions.DockBayCount,
};

// ---- WMS Core + input driver DI --------------------------------------------------------------
// Order matters: Application first (live-state singletons + handlers), then Infrastructure (persistence,
// gateway, seam adapters, catalog provider), then the Simulation driver (registers the accelerated
// SimulationClock as the core's IClock + the tick-loop hosted service).
builder.Services.AddForgeApplication(operatorOptions);
builder.Services.AddForgeInfrastructure(infrastructureOptions);
builder.Services.AddSimulationDriver(options =>
{
    // Route the operator's initial inbound-arrival rate / demand multiplier into the driver (Req 20.5, 20.6).
    options.InitialArrivalRatePerHour = 12.0;
    options.DemandMultiplier = 1.0;
});

// ---- Api-layer seams the composition root owns -----------------------------------------------
// The Simulation catalog provider projects the seeded persistence state (Infrastructure) into the
// Simulation catalog shape (Forge.Simulation). It lives in the Api because only the Api references both
// Infrastructure and Simulation; the Simulation driver resolves it as ISimulationCatalogProvider.
var dockBays = ForgeInfrastructureDependencyInjection.SynthesizeDockBays(
    infrastructureOptions.Seed, infrastructureOptions.DockBayCount);
builder.Services.AddSingleton(sp => new SeededSimulationCatalogProvider(
    sp.GetRequiredService<IServiceScopeFactory>(), dockBays));
builder.Services.AddSingleton<ISimulationCatalogProvider>(sp =>
    sp.GetRequiredService<SeededSimulationCatalogProvider>());

// The SignalR client notifier bridges the Infrastructure publisher (which must not reference the hub)
// to IHubContext<SimulationHub> (Req 23.2, 23.4).
builder.Services.AddSingleton<ISimulationClientNotifier, SignalRClientNotifier>();
// The forecast review store makes produced forecasts available to the operator for review (Req 22.1).
builder.Services.AddSingleton<IForecastReviewStore, InMemoryForecastReviewStore>();
// Periodically repopulate the Simulation catalog so newly-arrived lots become temperature targets.
builder.Services.AddHostedService<CatalogRefreshService>();
// Broadcast agent/starship positions at a fixed wall interval so the web renderer can animate.
builder.Services.AddHostedService<PositionsUpdateHostedService>();
builder.Services.AddHostedService<InventoryUpdateHostedService>();

// ---- Transport ------------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddSignalR();

// ---- CORS for the Game web client -----------------------------------------------------------
// The Next.js web client runs on a different origin (default http://localhost:3000) and calls the
// engine REST endpoints + SignalR hub cross-origin, so the engine must return CORS headers. SignalR
// negotiate/websocket requires AllowCredentials, which is incompatible with AllowAnyOrigin, so we
// allow an explicit configured origin list (Forge:WebClientOrigins; defaults to Next.js dev ports).
var webClientOrigins =
    builder.Configuration.GetSection("Forge:WebClientOrigins").Get<string[]>()
    ?? new[] { "http://localhost:3000", "https://localhost:3000" };
builder.Services.AddCors(options =>
    options.AddPolicy("ForgeWebClient", policy =>
        policy.WithOrigins(webClientOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));

var app = builder.Build();

// ---- Startup: DB init + migrate + seed + install the live world (Req 26.2, 26.3, 26.5, 10.1) ----
// Run synchronously before serving so a database/seed failure fails startup with a descriptive error
// (Req 26.5) rather than letting the tick loop spin against an uninitialized store.
await ForgeStartup.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);

// ---- Endpoints ------------------------------------------------------------------------------
// CORS must run before the endpoints it applies to (both REST controllers and the SignalR hub).
app.UseCors("ForgeWebClient");

app.MapControllers();
app.MapHub<SimulationHub>("/hub/simulation");

// Once the host runs, the registered SimulationHostedService tick loop starts automatically and drives
// the core headless (Req 10.1, 1.6, 1.7).
await app.RunAsync();

/// <summary>
/// Exposed so the WebApplicationFactory-based integration/smoke tests (task 33.4) can boot the headless
/// Api in-process. The composition root itself is the top-level program above.
/// </summary>
public partial class Program;
