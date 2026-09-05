using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Application.Simulation;
using Forge.Domain.Common;
using Forge.Simulation.Arrivals;
using Forge.Simulation.Clock;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Microsoft.Extensions.Hosting;

namespace Forge.Simulation;

/// <summary>
/// The Phase-1 Simulation tick loop (design "Simulation host / tick loop"; Req 10.1, 10.4, 10.5).
/// </summary>
public sealed class SimulationHostedService : BackgroundService
{
    private readonly SimulationClock _clock;
    private readonly IWarehouseCommandGateway _gateway;
    private readonly ArrivalGenerator _arrivals;
    private readonly ColonyDemandSimulator _demand;
    private readonly TemperatureReadingGenerator _temperature;
    private readonly ISimulationCatalogProvider _catalog;
    private readonly SimulationDriverOptions _options;
    private readonly OperatorParameterState _operatorParameters;
    private readonly ITickStateProvider _tickState;
    private readonly Func<DateTimeOffset> _wallClock;

    private DateTimeOffset? _lastWallTick;

    public SimulationHostedService(
        SimulationClock clock,
        IWarehouseCommandGateway gateway,
        ArrivalGenerator arrivals,
        ColonyDemandSimulator demand,
        TemperatureReadingGenerator temperature,
        ISimulationCatalogProvider catalog,
        SimulationDriverOptions options,
        OperatorParameterState operatorParameters,
        ITickStateProvider tickState,
        Func<DateTimeOffset>? wallClock = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(temperature);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tickState);

        _clock = clock;
        _gateway = gateway;
        _arrivals = arrivals;
        _demand = demand;
        _temperature = temperature;
        _catalog = catalog;
        _options = options;
        _operatorParameters = operatorParameters ?? throw new ArgumentNullException(nameof(operatorParameters));
        _tickState = tickState;
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Pause() => _clock.Pause();

    public void Resume() => _clock.Resume();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _lastWallTick = _wallClock();

        // Align clock mode with operator sim-speed default (both default to 60×).
        ApplySimSpeedToClock(_operatorParameters.SimSpeed);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.LoopInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task TickOnceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = _wallClock();
        var previous = _lastWallTick ?? now;
        _lastWallTick = now;

        var wallDelta = now - previous;
        var windowStart = _clock.Now;
        var simDelta = _clock.Advance(wallDelta);
        if (simDelta <= TimeSpan.Zero)
        {
            return;
        }

        await GenerateInputsAsync(windowStart, simDelta, ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        await _gateway.ApplyTickRulesAsync(simDelta, ct).ConfigureAwait(false);

        // Train / ship inbound cargo is requested during lifecycle; materialize immediately so the
        // next tick (and this wall tick's follow-up) can see lots on the dock.
        await MaterializeTrainCargoIfNeededAsync(_clock.Now, ct).ConfigureAwait(false);
        await MaterializeShipInboundIfNeededAsync(_clock.Now, ct).ConfigureAwait(false);
    }

    private async Task GenerateInputsAsync(DateTimeOffset windowStart, TimeSpan simDelta, CancellationToken ct)
    {
        // Scheduled trains-per-day owns inbound when &gt; 0; legacy Poisson only otherwise.
        if (_operatorParameters.TrainsPerDay <= 0)
        {
            _arrivals.ArrivalRatePerHour = _operatorParameters.InboundRate;
            if (_catalog.GelTypes.Count > 0 && _catalog.DockBays.Count > 0)
            {
                await _arrivals.GenerateAsync(windowStart, simDelta, ct).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (_catalog.Colonies.Count > 0)
        {
            await _demand
                .GenerateAsync(
                    _catalog.Colonies,
                    windowStart,
                    simDelta,
                    _operatorParameters.DemandMultiplier,
                    ct)
                .ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (_catalog.TemperatureTargets.Count > 0)
        {
            await _temperature
                .GenerateAsync(_catalog.TemperatureTargets, windowStart, simDelta, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task MaterializeTrainCargoIfNeededAsync(DateTimeOffset now, CancellationToken ct)
    {
        var state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        var train = state.Train;
        if (!train.NeedsCargoMaterialization || train.CargoMaterialized)
        {
            return;
        }

        if (_catalog.GelTypes.Count == 0 || _catalog.DockBays.Count == 0)
        {
            train.NeedsCargoMaterialization = false;
            return;
        }

        var before = new HashSet<GelLotId>(state.InboundQueueLotIds);
        int lotCount = Math.Max(
            1,
            (int)Math.Round(
                VisualSimulationConstants.TrainLotsPerVisit *
                Math.Clamp(_operatorParameters.DemandMultiplier, 0.5, 3.0)));

        await _arrivals
            .GenerateTrainBatchAsync(now, _operatorParameters.DemandMultiplier, lotCount, ct)
            .ConfigureAwait(false);

        // Re-read queue after receipts enqueued put-aways.
        state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        var newLots = new List<GelLotId>();
        foreach (var id in state.InboundQueueLotIds)
        {
            if (!before.Contains(id))
            {
                newLots.Add(id);
            }
        }

        state.Train.MarkCargoMaterialized(newLots, now);
    }

    private async Task MaterializeShipInboundIfNeededAsync(DateTimeOffset now, CancellationToken ct)
    {
        var state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        if (_catalog.GelTypes.Count == 0 || _catalog.DockBays.Count == 0)
        {
            foreach (var rt in state.StarshipRuntimes.Values)
            {
                if (rt.NeedsInboundMaterialization)
                {
                    rt.NeedsInboundMaterialization = false;
                    rt.PendingInboundLotCount = 0;
                    rt.UnloadRemaining = 0;
                }
            }

            return;
        }

        foreach (var ship in state.Starships.OrderBy(s => s.Id))
        {
            if (!state.StarshipRuntimes.TryGetValue(ship.Id, out var rt))
            {
                continue;
            }

            if (!rt.NeedsInboundMaterialization || rt.InboundCargoMaterialized)
            {
                continue;
            }

            int lotCount = Math.Max(1, rt.PendingInboundLotCount);
            var before = new HashSet<GelLotId>(state.InboundQueueLotIds);

            await _arrivals
                .GenerateTrainBatchAsync(now, _operatorParameters.DemandMultiplier, lotCount, ct)
                .ConfigureAwait(false);

            state = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);
            if (state is null || !state.StarshipRuntimes.TryGetValue(ship.Id, out rt))
            {
                return;
            }

            var newLots = new List<GelLotId>();
            foreach (var id in state.InboundQueueLotIds)
            {
                if (!before.Contains(id))
                {
                    newLots.Add(id);
                }
            }

            rt.MarkInboundMaterialized(newLots);
            rt.PhaseEnteredAt = now; // restart approach with cargo aboard
            _ = ship.SetLoadedQuantity(Math.Min(ship.CargoCapacity, newLots.Count));
        }
    }

    private void ApplySimSpeedToClock(double simSpeed)
    {
        if (simSpeed <= 0)
        {
            _clock.Configure(ClockMode.Paused, 1.0);
        }
        else if (Math.Abs(simSpeed - 1.0) < 0.0001)
        {
            _clock.Configure(ClockMode.RealTime, 1.0);
        }
        else
        {
            _clock.Configure(ClockMode.Accelerated, simSpeed);
        }
    }
}
