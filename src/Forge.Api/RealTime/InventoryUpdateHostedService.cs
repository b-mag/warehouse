using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Contracts.Dtos;
using Forge.Domain.ColdChain;
using Forge.Domain.Gels;
using Forge.Infrastructure.RealTime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.Api.RealTime;

/// <summary>
/// Pushes zone + lot inventory to clients so holding areas update after put-away completes.
/// Runs slower than PositionsUpdate to keep EF load modest.
/// </summary>
public sealed class InventoryUpdateHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ISimulationClientNotifier _notifier;

    public InventoryUpdateHostedService(
        IServiceScopeFactory scopes,
        ISimulationClientNotifier notifier)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Transient failures must not stop the pump.
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var zones = scope.ServiceProvider.GetRequiredService<IZoneRepository>();
        var lots = scope.ServiceProvider.GetRequiredService<IGelLotRepository>();

        var zoneList = await zones.ListAllAsync(ct).ConfigureAwait(false);
        var lotList = await lots.ListAllAsync(ct).ConfigureAwait(false);

        var payload = new InventoryUpdateDto(
            Zones: zoneList.Select(ToZoneDto).ToArray(),
            Lots: lotList.Select(ToLotDto).ToArray());

        await _notifier.NotifyAsync("InventoryUpdate", payload, ct).ConfigureAwait(false);
    }

    private static TemperatureZoneDto ToZoneDto(TemperatureZone zone) => new(
        Id: zone.Id.Value,
        MinC: zone.AllowableRange.MinCelsius,
        MaxC: zone.AllowableRange.MaxCelsius,
        Capacity: zone.Capacity,
        Stored: zone.StoredQuantity);

    private static GelLotDto ToLotDto(GelLot lot) => new(
        Id: lot.Id.Value,
        GelTypeId: lot.GelTypeId.Value,
        ExpiresAt: lot.ExpiresAt,
        Quantity: lot.Quantity,
        IsExpired: lot.IsExpired,
        AtRisk: lot.AtRisk,
        ZoneId: lot.AssignedZoneId is { } z ? z.Value : null);
}
