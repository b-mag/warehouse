using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Simulation.Arrivals;
using Forge.Simulation.Clock;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Microsoft.Extensions.Hosting;

namespace Forge.Simulation;

/// <summary>
/// The Phase-1 Simulation tick loop (design "Simulation host / tick loop"; Req 10.1, 10.4, 10.5).
/// A hosted background service that, on each wall-clock loop iteration:
/// <list type="number">
///   <item><description>
///     measures the wall-clock delta since the previous iteration and asks the accelerated
///     <see cref="SimulationClock"/> to <see cref="SimulationClock.Advance(TimeSpan)"/> by it,
///     receiving the applied <i>simulated</i> delta (zero while paused — Req 10.5);
///   </description></item>
///   <item><description>
///     while that applied delta is positive, generates inbound arrivals, colony demand, and
///     temperature readings for the span and issues them to the WMS Core through
///     <see cref="IWarehouseCommandGateway"/> (Req 11.1, 12.2, 6.2);
///   </description></item>
///   <item><description>
///     then invokes core per-tick rule application via
///     <see cref="IWarehouseCommandGateway.ApplyTickRulesAsync(TimeSpan, System.Threading.CancellationToken)"/>
///     for the same simulated delta (Req 10.4).
///   </description></item>
/// </list>
/// <para>
/// <b>Pause/resume (Req 10.5).</b> Pausing is a property of the clock: while paused, <c>Advance</c>
/// returns <see cref="TimeSpan.Zero"/>, so a tick generates nothing and does not apply rules — the
/// loop keeps spinning cheaply until the clock is resumed. <see cref="Pause"/>/<see cref="Resume"/>
/// forward to the clock for convenience.
/// </para>
/// <para>
/// <b>Testability / determinism.</b> The loop's wall-time source (<see cref="_wallClock"/>) and the
/// inter-iteration delay are injectable, and a single iteration is exposed as
/// <see cref="TickOnceAsync(System.Threading.CancellationToken)"/>, so tests can drive the loop step
/// by step against a controllable clock with no dependence on real wall-clock timing.
/// </para>
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
    private readonly Func<DateTimeOffset> _wallClock;

    // The wall-clock instant of the previous iteration; the delta between it and "now" is what the
    // simulated clock scales. Null until the first tick establishes a baseline (the first tick applies
    // no delta so a startup pause does not fast-forward simulated time).
    private DateTimeOffset? _lastWallTick;

    /// <summary>
    /// Create the tick loop over the accelerated clock, the four generators, the catalog seam, and the
    /// core command gateway.
    /// </summary>
    /// <param name="clock">The accelerated simulation clock (also registered as the core's <see cref="IClock"/>).</param>
    /// <param name="gateway">The WMS Core command entrypoint used to issue inputs and apply tick rules.</param>
    /// <param name="arrivals">The inbound-arrival generator.</param>
    /// <param name="demand">The authoritative colony-demand generator.</param>
    /// <param name="temperature">The temperature-reading generator.</param>
    /// <param name="catalog">The catalog seam supplying what to generate against each tick.</param>
    /// <param name="options">Tunable seeds, operator parameters, and loop cadence.</param>
    /// <param name="wallClock">
    /// The wall-time source used to measure iteration deltas; defaults to <see cref="DateTimeOffset.UtcNow"/>.
    /// Injectable so tests advance it deterministically.
    /// </param>
    public SimulationHostedService(
        SimulationClock clock,
        IWarehouseCommandGateway gateway,
        ArrivalGenerator arrivals,
        ColonyDemandSimulator demand,
        TemperatureReadingGenerator temperature,
        ISimulationCatalogProvider catalog,
        SimulationDriverOptions options,
        OperatorParameterState operatorParameters,
        Func<DateTimeOffset>? wallClock = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(arrivals);
        ArgumentNullException.ThrowIfNull(demand);
        ArgumentNullException.ThrowIfNull(temperature);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        _clock = clock;
        _gateway = gateway;
        _arrivals = arrivals;
        _demand = demand;
        _temperature = temperature;
        _catalog = catalog;
        _options = options;
        _operatorParameters = operatorParameters ?? throw new ArgumentNullException(nameof(operatorParameters));
        _wallClock = wallClock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Pause simulated-time advancement; ticks become no-ops until <see cref="Resume"/> (Req 10.5).</summary>
    public void Pause() => _clock.Pause();

    /// <summary>Resume simulated-time advancement after a <see cref="Pause"/>.</summary>
    public void Resume() => _clock.Resume();

    /// <summary>
    /// The background loop: repeatedly run one tick then wait <see cref="SimulationDriverOptions.LoopInterval"/>,
    /// until the host requests stop (Req 10.1). Cancellation ends the loop promptly.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Establish the baseline so the first measured delta is small, not "since epoch".
        _lastWallTick = _wallClock();

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(_options.LoopInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop requested during the inter-iteration wait; exit the loop cleanly.
                break;
            }
        }
    }

    /// <summary>
    /// Run exactly one tick: measure the wall delta since the last tick, advance the clock, and — only
    /// if the applied simulated delta is positive — generate inputs for the span and apply core tick
    /// rules for that same delta (Req 10.4, 10.5, 11.1, 12.2, 6.2). Exposed for deterministic testing.
    /// </summary>
    /// <param name="ct">Cancellation token; observed between steps.</param>
    public async Task TickOnceAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = _wallClock();
        var previous = _lastWallTick ?? now;
        _lastWallTick = now;

        var wallDelta = now - previous;

        // The simulated span the tick covers begins at the clock's current time.
        var windowStart = _clock.Now;

        // Convert wall time to simulated time via the clock's mode/factor. Paused → zero (Req 10.5).
        var simDelta = _clock.Advance(wallDelta);
        if (simDelta <= TimeSpan.Zero)
        {
            // Paused or no elapsed wall time: generate nothing and do not apply rules.
            return;
        }

        // 1) Generate driver inputs for the elapsed simulated span and issue them to the core.
        await GenerateInputsAsync(windowStart, simDelta, ct).ConfigureAwait(false);

        // 2) Invoke core per-tick rule application for the same simulated delta (Req 10.4).
        ct.ThrowIfCancellationRequested();
        await _gateway.ApplyTickRulesAsync(simDelta, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Issue the three generated input streams for the span <c>[windowStart, windowStart + simDelta)</c>
    /// against the current catalog. Ordering is fixed (arrivals, then demand, then temperature) so a
    /// given (state, delta, seed) triple always produces the same sequence (reproducibility — Req 12.7).
    /// </summary>
    private async Task GenerateInputsAsync(DateTimeOffset windowStart, TimeSpan simDelta, CancellationToken ct)
    {
        // Inbound arrivals at the current operator arrival rate (Req 11.1, 20.5).
        _arrivals.ArrivalRatePerHour = _operatorParameters.InboundRate;
        if (_catalog.GelTypes.Count > 0 && _catalog.DockBays.Count > 0)
        {
            await _arrivals.GenerateAsync(windowStart, simDelta, ct).ConfigureAwait(false);
        }

        // Authoritative colony demand scaled by the operator multiplier (Req 12.2, 20.6).
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

        // Temperature readings per lot/zone (Req 6.2).
        ct.ThrowIfCancellationRequested();
        if (_catalog.TemperatureTargets.Count > 0)
        {
            await _temperature
                .GenerateAsync(_catalog.TemperatureTargets, windowStart, simDelta, ct)
                .ConfigureAwait(false);
        }
    }
}
