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
    private static readonly LoadingWindowDto[] EmptyWindows = Array.Empty<LoadingWindowDto>();

    private readonly ITickStateProvider _tickState;
    private readonly IClock _clock;
    private readonly ISimulationClientNotifier _notifier;

    private string? _lastFingerprint;

    public PositionsUpdateHostedService(
        ITickStateProvider tickState,
        IClock clock,
        ISimulationClientNotifier notifier,
        OperatorParameterState operatorParameters)
    {
        _tickState = tickState ?? throw new ArgumentNullException(nameof(tickState));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _ = operatorParameters;
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

        // Slim starship projection: empty Windows on the high-frequency channel.
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
                    Windows: EmptyWindows,
                    Phase: phase,
                    DockIndex: dockIndex);
            })
            .ToArray();

        var trainRt = tickState.Train;
        var trainLots = trainRt.Phase is TrainPhases.Away or TrainPhases.Departing
            ? Array.Empty<Guid>()
            : trainRt.LotIds.Select(l => l.Value).ToArray();

        var train = new TrainDto(
            Id: trainRt.Id,
            Phase: trainRt.Phase,
            CarCount: trainRt.CarCount,
            LotIds: trainLots);

        var inbound = tickState.InboundQueueLotIds.Select(l => l.Value).ToArray();
        var transit = tickState.InTransitLotIds.Select(l => l.Value).ToArray();
        var simulatedAt = _clock.Now;

        var fingerprint = BuildFingerprint(agents, starships, inbound, transit, train, simulatedAt);
        if (fingerprint == _lastFingerprint)
        {
            return;
        }

        _lastFingerprint = fingerprint;

        var payload = new PositionsUpdateDto(
            Agents: agents,
            Starships: starships,
            InboundQueueLotIds: inbound,
            InTransitLotIds: transit,
            Train: train,
            SimulatedAt: simulatedAt);

        await _notifier.NotifyAsync("PositionsUpdate", payload, ct).ConfigureAwait(false);
    }

    private static string BuildFingerprint(
        AgentDto[] agents,
        StarshipDto[] starships,
        Guid[] inbound,
        Guid[] transit,
        TrainDto train,
        DateTimeOffset simulatedAt)
    {
        // Include second-resolution clock so HUD advances under acceleration even when idle.
        var sb = new System.Text.StringBuilder(256);
        sb.Append(simulatedAt.UtcTicks / TimeSpan.TicksPerSecond).Append('|');
        sb.Append(train.Phase).Append(':').Append(train.CarCount).Append(':').Append(train.LotIds.Count).Append('|');
        sb.Append(inbound.Length).Append(':').Append(transit.Length).Append('|');
        foreach (var a in agents)
        {
            sb.Append(a.Id).Append('@').Append(a.X).Append(',').Append(a.Y)
                .Append('p').Append(a.PathCells.Count)
                .Append('c').Append(a.CarryingLotId).Append(';');
        }

        foreach (var s in starships)
        {
            sb.Append(s.Id).Append(':').Append(s.Phase).Append(':')
                .Append(s.DockIndex).Append(':').Append(s.Loaded).Append(';');
        }

        return sb.ToString();
    }

    private static Guid? TryCarryingLotId(TickState tickState, AgentId agentId)
    {
        if (!tickState.AgentTasks.TryGetValue(agentId, out var taskId))
        {
            return null;
        }

        if (tickState.PutAwayTaskLotLinks.TryGetValue(taskId, out var lotId))
        {
            for (int i = 0; i < tickState.InTransitLotIds.Count; i++)
            {
                if (tickState.InTransitLotIds[i].Equals(lotId))
                {
                    return lotId.Value;
                }
            }

            return null;
        }

        if (tickState.PickTaskLotLinks.TryGetValue(taskId, out var pickLot))
        {
            for (int i = 0; i < tickState.InTransitLotIds.Count; i++)
            {
                if (tickState.InTransitLotIds[i].Equals(pickLot))
                {
                    return pickLot.Value;
                }
            }

            return null;
        }

        return null;
    }
}
