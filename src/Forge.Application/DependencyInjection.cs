using Forge.Application.Abstractions;
using Forge.Application.ColdChain;
using Forge.Application.Docks;
using Forge.Application.Forecasting;
using Forge.Application.Inbound;
using Forge.Application.Loading;
using Forge.Application.OperatorParameters;
using Forge.Application.Orders;
using Forge.Application.Simulation;
using Forge.Application.Slotting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Application;

/// <summary>
/// Composition-root wiring for the WMS Core <b>Application</b> layer (task 33.3). Registers the
/// command/event use-case handlers, the stateless Application services, the pluggable slotting
/// strategies, and the components that hold <b>live shared state</b> across the whole engine
/// (operator-parameter state, warehouse metrics, dock scheduler).
/// <para>
/// <b>Lifetime rationale.</b>
/// <list type="bullet">
///   <item><b>Handlers are scoped.</b> They orchestrate the scoped repositories / <c>DbContext</c>
///     (via <c>IUnitOfWork</c>), so they must share the same per-operation scope the gateway opens.
///     Resolving a handler from a scope gives it a fresh <c>DbContext</c> for that one operation.</item>
///   <item><b>Live-state components are singletons.</b> <see cref="OperatorParameterState"/>,
///     <see cref="WarehouseMetrics"/>, and <see cref="DockScheduler"/> hold state that must persist
///     across every operation and be observed identically by the tick loop and the snapshot query, so
///     they are single shared instances.</item>
///   <item><b>Stateless services / strategies are singletons.</b> <see cref="StarshipLoadingService"/>,
///     <see cref="ForecastingService"/>, the two <see cref="ISlottingStrategy"/> implementations, and
///     <see cref="OperatorParameterService"/> hold no per-operation state (the operator-parameter
///     service holds only references to the singleton state + clock), so a shared instance is safe.</item>
/// </list>
/// </para>
/// <para>
/// This method does <b>not</b> register the repositories, unit of work, clock, event bus, ML predictor,
/// gateway, or the Phase-2 seam adapters (path planner, reservation manager, forecast audit sink,
/// tick-state provider, gel-type catalog) — those are Infrastructure concerns wired by
/// <c>AddForgeInfrastructure</c>. It also does not register the two handlers whose construction needs an
/// Infrastructure-known value (the snapshot handler's primary dock bay), which the Infrastructure wiring
/// contributes. Keeping this split preserves the Application layer's Domain+Contracts-only reference
/// boundary.
/// </para>
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Register the WMS Core Application handlers, services, slotting strategies, and live-state
    /// components (task 33.3). Call before <c>AddForgeInfrastructure</c> and <c>AddSimulationDriver</c>.
    /// </summary>
    /// <param name="services">The service collection to add registrations to.</param>
    /// <param name="operatorOptions">
    /// Deployment-configured operator-parameter bounds + initial values (worker max, modeled dock bays,
    /// initial values). Seeds the singleton <see cref="OperatorParameterState"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddForgeApplication(
        this IServiceCollection services,
        OperatorParameterOptions operatorOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(operatorOptions);

        // ---- Live shared state (singletons). ----
        services.TryAddSingleton(operatorOptions);
        services.TryAddSingleton<OperatorParameterState>();
        services.TryAddSingleton<WarehouseMetrics>();
        services.TryAddSingleton<DockScheduler>();

        // ---- Stateless services / strategies (singletons). ----
        services.TryAddSingleton<StarshipLoadingService>();
        services.TryAddSingleton<ForecastingService>();
        // OperatorParameterService takes the singleton state + the (optionally) wired IClock; the clock
        // comes from the Simulation driver / Infrastructure. Registered singleton so the applied values
        // and the clock configuration share one instance with the update handler and the driver.
        services.TryAddSingleton(sp => new OperatorParameterService(
            sp.GetRequiredService<OperatorParameterState>(),
            sp.GetService<IClock>()));

        // The two Phase-1 slotting strategies. Both are registered so the active strategy can be
        // selected by key (Req 20.7); the default (velocity-affinity) is exposed as ISlottingStrategy.
        services.TryAddSingleton<VelocityAffinitySlottingStrategy>();
        services.TryAddSingleton<NaiveFirstAvailableStrategy>();
        services.TryAddSingleton<ISlottingStrategy>(sp =>
            sp.GetRequiredService<VelocityAffinitySlottingStrategy>());

        // ---- Use-case handlers (scoped: they touch scoped repositories / DbContext). ----
        services.TryAddScoped<CreateColonyOrderHandler>();
        services.TryAddScoped<RecordInboundGelReceiptHandler>();
        services.TryAddScoped<RecordTemperatureReadingHandler>();
        services.TryAddScoped<ApplyTickRulesHandler>();

        // ---- Handlers that touch no repository can be singletons. ----
        // The update-operator-parameter and submit-forecast-decision handlers depend only on the
        // singleton live-state / seam components (parameter service, event bus, clock, audit sink), so
        // a shared instance is correct and avoids opening a scope for a parameter change.
        services.TryAddSingleton<UpdateOperatorParameterHandler>();
        services.TryAddSingleton<SubmitForecastDecisionHandler>();

        return services;
    }
}
