using Forge.Application.Abstractions;
using Forge.Application.Simulation;
using Forge.Application.OperatorParameters;
using Forge.Contracts.Dtos;
using Forge.Infrastructure.RealTime;
using Forge.Domain.Common;

using Microsoft.Extensions.Hosting;

namespace Forge.Api.RealTime;

/// <summary>
/// Periodically broadcasts a lightweight real-time positions payload to SignalR clients
/// so the web renderer can animate agent movement without re-fetching the full snapshot.
/// </summary>
public sealed class PositionsUpdateHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);

    private readonly ITickStateProvider _tickState;
    private readonly IClock _clock;
    private readonly ISimulationClientNotifier _notifier;
    private readonly OperatorParameterState _operatorParameters;

    public PositionsUpdateHostedService(
        ITickStateProvider tickState,
        IClock clock,
        ISimulationClientNotifier notifier,
        OperatorParameterState operatorParameters)
    {
        _tickState = tickState ?? throw new ArgumentNullException(nameof(tickState));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _operatorParameters = operatorParameters ?? throw new ArgumentNullException(nameof(operatorParameters));
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
                // A transient failure must not stop the simulation from advancing.
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
        var tickState = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
        if (tickState is null)
        {
            return;
        }

        var agents = tickState.Agents
            .Select(a => new AgentDto(
                Id: a.Id.Value,
                X: a.Position.X,
                Y: a.Position.Y,
                PathCells: a.CurrentPath is { } path
                    ? path.Cells.Select(c => new CellDto(c.X, c.Y)).ToArray()
                    : Array.Empty<CellDto>(),
                CellsPerSecond: a.CellsPerSecond,
                Phase: "Active",
                CarryingLotId: TryCarryingLotId(tickState, a.Id)))
            .ToArray();

        var starships = tickState.Starships
            .OrderBy(s => s.Id)
            .Select(ship =>
            {
                tickState.StarshipRuntimes.TryGetValue(ship.Id, out var rt);
                string phase = rt?.Phase ?? StarshipPhases.Away;
                int dockIndex = rt?.DockIndex ?? -1;

                return new StarshipDto(
                    Id: ship.Id.Value,
                    Capacity: ship.CargoCapacity,
                    Loaded: ship.LoadedQuantity,
                    DestinationColony: ship.Destination.Value,
                    Windows: ship.Windows.Select(w => new LoadingWindowDto(w.Start, w.End)).ToArray(),
                    Phase: phase,
                    DockIndex: dockIndex);
            })
            .ToArray();

        var payload = new PositionsUpdateDto(
            Agents: agents,
            Starships: starships,
            InboundQueueLotIds: tickState.InboundQueueLotIds.Select(l => l.Value).ToArray(),
            InTransitLotIds: tickState.InTransitLotIds.Select(l => l.Value).ToArray());

        await _notifier.NotifyAsync("PositionsUpdate", payload, ct).ConfigureAwait(false);
    }

    private static Guid? TryCarryingLotId(TickState tickState, AgentId agentId)
    {
        if (!tickState.AgentTasks.TryGetValue(agentId, out var taskId))
        {
            return null;
        }

        if (tickState.PutAwayTaskLotLinks.TryGetValue(taskId, out var lotId))
        {
            return lotId.Value;
        }

        if (tickState.PickTaskLotLinks.TryGetValue(taskId, out var pickLot))
        {
            return pickLot.Value;
        }

        // Any in-flight PutAway/Pick still shows a carried cube even before a lot link exists.
        return taskId.Value;
    }
}
