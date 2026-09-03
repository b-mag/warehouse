using Forge.Api.Simulation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Forge.Api.Startup;

/// <summary>
/// A background service that periodically refreshes the seeded Simulation catalog (task 33.3). The
/// <see cref="SeededSimulationCatalogProvider"/> serves the tick loop synchronous, cached catalog
/// snapshots; this service repopulates them off the tick-loop thread so lots created by inbound
/// arrivals become temperature-reading targets on a later tick without blocking generation (Req 6.2).
/// <para>
/// A refresh failure is logged and swallowed — a transient database hiccup must never stop the
/// simulation from advancing; the previous cached snapshot simply stays in effect until the next
/// successful refresh.
/// </para>
/// </summary>
public sealed class CatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly SeededSimulationCatalogProvider _catalog;
    private readonly ILogger<CatalogRefreshService> _logger;

    /// <summary>Create the refresh loop over the catalog provider it repopulates.</summary>
    public CatalogRefreshService(
        SeededSimulationCatalogProvider catalog,
        ILogger<CatalogRefreshService> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _catalog.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient failure must not stop the simulation; keep the last good snapshot.
                _logger.LogWarning(ex, "Simulation catalog refresh failed; retaining the previous snapshot.");
            }
        }
    }
}
