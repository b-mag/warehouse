using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Simulation;
using Forge.Simulation.Arrivals;
using Forge.Simulation.Clock;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Forge.Application.OperatorParameters;

namespace Forge.Tests.Simulation;

/// <summary>
/// Unit tests for the <see cref="SimulationHostedService"/> tick loop (Req 10.1, 10.4, 10.5, 11.1,
/// 12.2). The loop's wall-time source is injected and a single iteration is driven via
/// <see cref="SimulationHostedService.TickOnceAsync"/>, so every test is fully deterministic and never
/// depends on real wall-clock timing.
/// </summary>
public sealed class SimulationHostedServiceTests
{
    /// <summary>
    /// A spy <see cref="IWarehouseCommandGateway"/> recording, in order, the inputs generated and each
    /// <see cref="ApplyTickRulesAsync"/> call's simulated delta. Every entrypoint succeeds.
    /// </summary>
    private sealed class SpyGateway : IWarehouseCommandGateway
    {
        public List<RecordInboundGelReceiptCommand> Receipts { get; } = [];
        public List<CreateColonyOrderCommand> Orders { get; } = [];
        public List<RecordTemperatureReadingCommand> Readings { get; } = [];
        public List<TimeSpan> TickRuleDeltas { get; } = [];

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(CreateColonyOrderCommand cmd, CancellationToken ct = default)
        {
            Orders.Add(cmd);
            return Task.FromResult(Result<ColonyOrderId>.Success(ColonyOrderId.New()));
        }

        public Task<Result> RecordInboundGelReceiptAsync(RecordInboundGelReceiptCommand cmd, CancellationToken ct = default)
        {
            Receipts.Add(cmd);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> RecordTemperatureReadingAsync(RecordTemperatureReadingCommand cmd, CancellationToken ct = default)
        {
            Readings.Add(cmd);
            return Task.FromResult(Result.Success());
        }

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default)
        {
            TickRuleDeltas.Add(simDelta);
            return Task.FromResult(Result.Success());
        }

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>A fixed catalog with one gel type, one dock, one colony, and one temperature target.</summary>
    private sealed class FakeCatalog : ISimulationCatalogProvider
    {
        public IReadOnlyList<GelTypeId> GelTypes { get; }
        public IReadOnlyList<DockBayId> DockBays { get; }
        public IReadOnlyList<ColonyDemandSource> Colonies { get; }
        public IReadOnlyList<TemperatureReadingTarget> TemperatureTargets { get; }

        public FakeCatalog()
        {
            var gel = new GelTypeId(new Guid("11111111-1111-1111-1111-111111111111"));
            GelTypes = [gel];
            DockBays = [new DockBayId(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))];

            var profile = DemandProfile.Create(
                new Dictionary<GelTypeId, double> { [gel] = 100.0 },
                []).Value;
            Colonies = [new ColonyDemandSource(new ColonyId(new Guid("cccccccc-cccc-cccc-cccc-cccccccccccc")), profile)];

            TemperatureTargets =
            [
                new TemperatureReadingTarget(
                    new GelLotId(new Guid("dddddddd-dddd-dddd-dddd-dddddddddddd")),
                    new TemperatureRange(-20m, -10m)),
            ];
        }
    }

    private static readonly DateTimeOffset SimStart = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A mutable wall-time source the tests advance explicitly, so the measured wall delta — and hence
    /// the applied simulated delta — is fully controlled with no dependence on real time.
    /// </summary>
    private sealed class ManualWallClock
    {
        private DateTimeOffset _now;
        public ManualWallClock(DateTimeOffset start) => _now = start;
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static (SimulationHostedService Host, SpyGateway Gateway, SimulationClock Clock, ManualWallClock Wall)
        Build(ClockMode mode = ClockMode.Accelerated, double factor = 60.0)
    {
        var gateway = new SpyGateway();
        var clock = new SimulationClock(SimStart, mode, factor);
        var catalog = new FakeCatalog();
        var options = new SimulationDriverOptions { InitialArrivalRatePerHour = 1000.0, DemandMultiplier = 1.0 };

        var operatorParameters = new OperatorParameterState(
            new OperatorParameterOptions
            {
                WorkerMax = 25,
                ModeledDockBays = 4,
                InitialInboundRate = options.InitialArrivalRatePerHour,
                InitialDemandMultiplier = options.DemandMultiplier,
            });

        var arrivals = new ArrivalGenerator(gateway, options.ArrivalSeed, catalog.GelTypes, catalog.DockBays);
        var demand = new ColonyDemandSimulator(gateway, options.DemandSeed);
        var temperature = new TemperatureReadingGenerator(gateway, options.TemperatureSeed);

        var wall = new ManualWallClock(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var host = new SimulationHostedService(
            clock,
            gateway,
            arrivals,
            demand,
            temperature,
            catalog,
            options,
            operatorParameters,
            wall.Now);

        return (host, gateway, clock, wall);
    }

    [Fact]
    public async Task PositiveDelta_GeneratesInputs_ThenAppliesTickRulesWithThatDelta()
    {
        var (host, gateway, _, wall) = Build(ClockMode.Accelerated, factor: 60.0);

        // First tick establishes the baseline (delta 0) — no generation, no rule application.
        await host.TickOnceAsync();
        Assert.Empty(gateway.TickRuleDeltas);

        // Advance wall time by 1 minute; accelerated ×60 => 1 simulated hour applied delta.
        wall.Advance(TimeSpan.FromMinutes(1));
        await host.TickOnceAsync();

        var expectedSimDelta = TimeSpan.FromMinutes(1) * 60.0;

        // Rules were applied exactly once, for exactly the applied simulated delta (Req 10.4).
        Assert.Single(gateway.TickRuleDeltas);
        Assert.Equal(expectedSimDelta, gateway.TickRuleDeltas[0]);

        // Inputs were generated over the span for that delta (Req 11.1, 12.2, 6.2).
        Assert.NotEmpty(gateway.Receipts);   // arrivals (rate 1000/hr over 1hr => many)
        Assert.NotEmpty(gateway.Orders);     // colony demand (one order per whole sim hour)
        Assert.NotEmpty(gateway.Readings);   // temperature readings (15-min cadence over 1hr)
    }

    [Fact]
    public async Task PausedClock_ZeroAppliedDelta_GeneratesNothing_AndDoesNotApplyTickRules()
    {
        var (host, gateway, clock, wall) = Build(ClockMode.Accelerated, factor: 60.0);

        // Baseline, then pause before any advancing tick (Req 10.5).
        await host.TickOnceAsync();
        clock.Pause();

        // Even though wall time elapsed, a paused clock applies zero delta.
        wall.Advance(TimeSpan.FromMinutes(5));
        await host.TickOnceAsync();

        Assert.Empty(gateway.TickRuleDeltas);
        Assert.Empty(gateway.Receipts);
        Assert.Empty(gateway.Orders);
        Assert.Empty(gateway.Readings);

        // Simulated time did not move while paused.
        Assert.Equal(SimStart, clock.Now);
    }

    [Fact]
    public async Task Resume_AfterPause_ResumesGenerationAndRuleApplication()
    {
        var (host, gateway, clock, wall) = Build(ClockMode.Accelerated, factor: 60.0);

        await host.TickOnceAsync();     // baseline
        clock.Pause();
        wall.Advance(TimeSpan.FromMinutes(5));
        await host.TickOnceAsync();     // paused: no-op
        Assert.Empty(gateway.TickRuleDeltas);

        clock.Resume();
        wall.Advance(TimeSpan.FromMinutes(1));
        await host.TickOnceAsync();     // running again

        Assert.Single(gateway.TickRuleDeltas);
        Assert.Equal(TimeSpan.FromMinutes(1) * 60.0, gateway.TickRuleDeltas[0]);
    }

    [Fact]
    public async Task ZeroWallDelta_AppliesNoRules()
    {
        var (host, gateway, _, _) = Build();

        await host.TickOnceAsync();     // baseline
        await host.TickOnceAsync();     // no wall time elapsed => zero delta

        Assert.Empty(gateway.TickRuleDeltas);
        Assert.Empty(gateway.Receipts);
    }

    [Fact]
    public async Task StartAsync_ThenStopAsync_RunsAndStopsTheLoopCleanly()
    {
        var (host, _, _, _) = Build();

        // The BackgroundService lifecycle should start and stop without throwing.
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Driver_StartStop_DelegatesToHostedServiceLifecycle()
    {
        var (host, _, clock, _) = Build();
        var driver = new SimulationInputDriver(host);

        await driver.StartAsync(CancellationToken.None);

        // Pause/resume through the driver forwards to the clock.
        driver.Pause();
        Assert.Equal(ClockMode.Paused, clock.Mode);
        driver.Resume();
        Assert.NotEqual(ClockMode.Paused, clock.Mode);

        await driver.StopAsync(CancellationToken.None);
    }
}
