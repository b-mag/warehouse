using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Application.ColdChain;
using Forge.Application.Inbound;
using Forge.Application.Orders;
using Forge.Application.Queries;
using Forge.Application.Simulation;
using Forge.Contracts.Dtos;
using Forge.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

namespace Forge.Infrastructure.Gateway;

/// <summary>
/// The concrete <see cref="IWarehouseCommandGateway"/> — the single command/query entrypoint the
/// wired input driver's tick loop and the REST controllers both drive the WMS Core through
/// (task 33.3; design "The Input Driver seam"). It composes the use-case handlers:
/// <list type="bullet">
///   <item><see cref="CreateColonyOrderAsync"/> → <see cref="CreateColonyOrderHandler"/></item>
///   <item><see cref="RecordInboundGelReceiptAsync"/> → <see cref="RecordInboundGelReceiptHandler"/></item>
///   <item><see cref="RecordTemperatureReadingAsync"/> → <see cref="RecordTemperatureReadingHandler"/></item>
///   <item><see cref="ApplyTickRulesAsync"/> → <see cref="ApplyTickRulesHandler"/></item>
///   <item><see cref="GetSnapshotAsync"/> → <see cref="GetSimulationSnapshotHandler"/></item>
/// </list>
///
/// <para><b>Scoping — the critical wiring correctness point.</b> The gateway is a <b>singleton</b>: the
/// Simulation tick loop is a singleton hosted service that holds it for the process lifetime and calls
/// it repeatedly. But the handlers touch <b>scoped</b> repositories backed by a scoped
/// <c>ForgeDbContext</c> (EF Core's <c>DbContext</c> is not safe to capture in a singleton, nor to use
/// concurrently). So this gateway <b>opens a fresh DI scope per operation</b> via
/// <see cref="IServiceScopeFactory"/> and resolves the handler (and, transitively, the scoped
/// repositories + <c>DbContext</c> + unit of work) from that scope. Each gateway call therefore gets
/// its own short-lived <c>DbContext</c> that is created and disposed within the call — the singleton
/// driver never captures a scoped <c>DbContext</c>, and two overlapping calls never share one. This is
/// the standard and correct pattern for invoking scoped EF Core work from a singleton background loop.
/// </para>
///
/// <para>The gateway itself holds no per-call state, so it is trivially safe as a singleton; all state
/// lives in the scoped context resolved per call (or in the explicitly-singleton live-state components
/// such as the metrics, dock scheduler, tick-state provider, and operator-parameter state, which are
/// designed to be shared).</para>
/// </summary>
public sealed class WarehouseCommandGateway : IWarehouseCommandGateway
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Create the gateway over the scope factory it opens a fresh scope from per operation.</summary>
    public WarehouseCommandGateway(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(
        CreateColonyOrderCommand cmd, CancellationToken ct = default) =>
        InScopeAsync(sp => sp.GetRequiredService<CreateColonyOrderHandler>().Handle(cmd, ct));

    /// <inheritdoc />
    public Task<Result> RecordInboundGelReceiptAsync(
        RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
        InScopeAsync(sp => sp.GetRequiredService<RecordInboundGelReceiptHandler>().HandleAsync(cmd, ct));

    /// <inheritdoc />
    public Task<Result> RecordTemperatureReadingAsync(
        RecordTemperatureReadingCommand cmd, CancellationToken ct = default) =>
        InScopeAsync(sp => sp.GetRequiredService<RecordTemperatureReadingHandler>().HandleAsync(cmd, ct));

    /// <inheritdoc />
    public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
        InScopeAsync(sp => sp.GetRequiredService<ApplyTickRulesHandler>().ApplyTickRulesAsync(simDelta, ct));

    /// <inheritdoc />
    public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default) =>
        InScopeAsync(sp => sp.GetRequiredService<GetSimulationSnapshotHandler>().HandleAsync(ct));

    // Open a fresh DI scope, run the handler resolved from it, and dispose the scope (and its scoped
    // DbContext) once the handler's task completes. Awaiting before disposal is essential so the
    // DbContext outlives the async work that uses it.
    private async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> operation)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await operation(scope.ServiceProvider).ConfigureAwait(false);
    }
}
