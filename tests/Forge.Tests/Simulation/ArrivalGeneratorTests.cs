using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.Common;
using Forge.Simulation.Arrivals;

namespace Forge.Tests.Simulation;

/// <summary>
/// Unit tests for the deterministic seeded <see cref="ArrivalGenerator"/> (Req 11.1, 14.1, 20.5).
/// Cover: arrivals scale with the rate, a zero rate produces none, and identical
/// seed + simulated window + rate reproduces an identical command sequence.
/// </summary>
public sealed class ArrivalGeneratorTests
{
    /// <summary>
    /// A fake <see cref="IWarehouseCommandGateway"/> capturing every issued
    /// <see cref="RecordInboundGelReceiptCommand"/> in issue order. Only the arrival entrypoint is
    /// exercised; the rest throw so an unexpected call surfaces immediately.
    /// </summary>
    private sealed class CapturingGateway : IWarehouseCommandGateway
    {
        public List<RecordInboundGelReceiptCommand> Receipts { get; } = [];

        public Task<Result> RecordInboundGelReceiptAsync(RecordInboundGelReceiptCommand cmd, CancellationToken ct = default)
        {
            Receipts.Add(cmd);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(CreateColonyOrderCommand cmd, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> RecordTemperatureReadingAsync(RecordTemperatureReadingCommand cmd, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static readonly DateTimeOffset WindowStart = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (IReadOnlyList<GelTypeId> Gels, IReadOnlyList<DockBayId> Docks) BuildCatalogs()
    {
        // Fixed ids (not New()) so a catalog is itself reproducible across generator instances.
        var gels = new List<GelTypeId>
        {
            new(new Guid("11111111-1111-1111-1111-111111111111")),
            new(new Guid("22222222-2222-2222-2222-222222222222")),
            new(new Guid("33333333-3333-3333-3333-333333333333")),
        };
        var docks = new List<DockBayId>
        {
            new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
        };
        return (gels, docks);
    }

    private static ArrivalGenerator NewGenerator(IWarehouseCommandGateway gateway, ulong seed, double rate)
    {
        var (gels, docks) = BuildCatalogs();
        return new ArrivalGenerator(gateway, seed, gels, docks, initialArrivalRatePerHour: rate);
    }

    [Fact]
    public async Task ZeroRate_ProducesNoArrivals()
    {
        var gateway = new CapturingGateway();
        var gen = NewGenerator(gateway, seed: 42, rate: 0.0);

        var issued = await gen.GenerateAsync(WindowStart, TimeSpan.FromHours(24));

        Assert.Empty(issued);
        Assert.Empty(gateway.Receipts);
    }

    [Fact]
    public void NonPositiveDelta_ProducesNoArrivals()
    {
        var gateway = new CapturingGateway();
        var gen = NewGenerator(gateway, seed: 7, rate: 100.0);

        Assert.Empty(gen.BuildCommands(WindowStart, TimeSpan.Zero));
        Assert.Empty(gen.BuildCommands(WindowStart, TimeSpan.FromHours(-5)));
    }

    [Fact]
    public void ArrivalsScaleWithRate()
    {
        // Average produced count over many independent windows should grow with the rate.
        // Compare a low rate against a 10x rate; expect meaningfully more arrivals at the higher rate.
        const int windows = 200;
        var span = TimeSpan.FromHours(1);

        long lowTotal = CountOverWindows(seed: 12345, rate: 2.0, windows, span);
        long highTotal = CountOverWindows(seed: 12345, rate: 20.0, windows, span);

        Assert.True(highTotal > lowTotal,
            $"Expected higher rate to yield more arrivals (low={lowTotal}, high={highTotal}).");

        // Sanity on magnitude: mean per window ~ rate*hours, so totals are near rate*windows.
        Assert.InRange(lowTotal, (long)(2.0 * windows * 0.5), (long)(2.0 * windows * 1.5));
        Assert.InRange(highTotal, (long)(20.0 * windows * 0.7), (long)(20.0 * windows * 1.3));

        static long CountOverWindows(ulong seed, double rate, int windows, TimeSpan span)
        {
            var gen = NewGenerator(new CapturingGateway(), seed, rate);
            long total = 0;
            for (int i = 0; i < windows; i++)
            {
                // Distinct, non-overlapping windows so each draws an independent stream.
                var start = WindowStart + TimeSpan.FromTicks(span.Ticks * i);
                total += gen.BuildCommands(start, span).Count;
            }
            return total;
        }
    }

    [Fact]
    public async Task IdenticalSeedTimeRate_ReproducesIdenticalSequence()
    {
        var span = TimeSpan.FromHours(12);

        var gatewayA = new CapturingGateway();
        var genA = NewGenerator(gatewayA, seed: 99, rate: 50.0);
        await genA.GenerateAsync(WindowStart, span);

        var gatewayB = new CapturingGateway();
        var genB = NewGenerator(gatewayB, seed: 99, rate: 50.0);
        await genB.GenerateAsync(WindowStart, span);

        // Same count, same values, same order.
        Assert.NotEmpty(gatewayA.Receipts);
        Assert.Equal(gatewayA.Receipts.Count, gatewayB.Receipts.Count);
        Assert.Equal(gatewayA.Receipts, gatewayB.Receipts);
    }

    [Fact]
    public void DifferentSeed_ChangesSequence()
    {
        var span = TimeSpan.FromHours(12);

        var a = NewGenerator(new CapturingGateway(), seed: 1, rate: 50.0).BuildCommands(WindowStart, span);
        var b = NewGenerator(new CapturingGateway(), seed: 2, rate: 50.0).BuildCommands(WindowStart, span);

        // Overwhelmingly likely to differ; guards against a stream ignoring the seed.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void IssuedCommands_UseOnlyCatalogGelTypesDockBaysAndValidQuantities()
    {
        var (gels, docks) = BuildCatalogs();
        var gen = NewGenerator(new CapturingGateway(), seed: 555, rate: 80.0);

        var cmds = gen.BuildCommands(WindowStart, TimeSpan.FromHours(24));

        Assert.NotEmpty(cmds);
        foreach (var cmd in cmds)
        {
            Assert.Contains(cmd.GelTypeId, gels);
            Assert.Contains(cmd.DockBayId, docks);
            Assert.True(cmd.Quantity >= 1, "Quantity must be >= 1.");
        }
    }
}
