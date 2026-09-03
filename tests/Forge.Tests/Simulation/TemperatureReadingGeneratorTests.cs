using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Simulation.Temperature;

using Xunit;

namespace Forge.Tests.Simulation;

// Feature: nutrient-forge, Task 27.5 — TemperatureReadingGenerator (Forge.Simulation).
//
// Covers Requirement 6.2 (per-lot temperature readings issued to the core via
// IWarehouseCommandGateway.RecordTemperatureReadingAsync) and the generator's determinism contract:
// identical seed + identical simulated span + identical lot/zone input reproduce an identical
// sequence of readings. The generator only emits readings; it never decides excursions (Req 6.3),
// which is core domain logic exercised elsewhere.
public sealed class TemperatureReadingGeneratorTests
{
    private static readonly DateTimeOffset Start = new(2350, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TemperatureReadingTarget Target(TemperatureRange range) =>
        new(GelLotId.New(), range);

    [Fact]
    public async Task Issues_readings_for_provided_lots_over_a_positive_delta()
    {
        var gateway = new RecordingGateway();
        var generator = new TemperatureReadingGenerator(gateway, seed: 1234);

        var lotA = Target(new TemperatureRange(0m, 4m));
        var lotB = Target(new TemperatureRange(-2m, 2m));

        // A one-hour delta at the 15-minute cadence yields readings at +15/+30/+45 min (t=0 and t=+60
        // are excluded because the loop samples strictly inside the span), i.e. 3 per lot.
        var count = await generator.GenerateAsync(new[] { lotA, lotB }, Start, TimeSpan.FromHours(1));

        Assert.Equal(6, count);
        Assert.Equal(6, gateway.Commands.Count);
        Assert.Contains(gateway.Commands, c => c.GelLotId.Equals(lotA.LotId));
        Assert.Contains(gateway.Commands, c => c.GelLotId.Equals(lotB.LotId));

        // Every reading carries a timestamp strictly inside the span.
        Assert.All(gateway.Commands, c =>
        {
            Assert.True(c.RecordedAt > Start);
            Assert.True(c.RecordedAt < Start + TimeSpan.FromHours(1));
        });
    }

    [Fact]
    public async Task Empty_lot_set_issues_nothing()
    {
        var gateway = new RecordingGateway();
        var generator = new TemperatureReadingGenerator(gateway, seed: 1);

        var count = await generator.GenerateAsync(
            Array.Empty<TemperatureReadingTarget>(), Start, TimeSpan.FromHours(6));

        Assert.Equal(0, count);
        Assert.Empty(gateway.Commands);
    }

    [Fact]
    public async Task Zero_delta_issues_nothing()
    {
        var gateway = new RecordingGateway();
        var generator = new TemperatureReadingGenerator(gateway, seed: 1);

        var count = await generator.GenerateAsync(
            new[] { Target(new TemperatureRange(0m, 4m)) }, Start, TimeSpan.Zero);

        Assert.Equal(0, count);
        Assert.Empty(gateway.Commands);
    }

    [Fact]
    public async Task Negative_delta_issues_nothing()
    {
        var gateway = new RecordingGateway();
        var generator = new TemperatureReadingGenerator(gateway, seed: 1);

        var count = await generator.GenerateAsync(
            new[] { Target(new TemperatureRange(0m, 4m)) }, Start, TimeSpan.FromMinutes(-30));

        Assert.Equal(0, count);
        Assert.Empty(gateway.Commands);
    }

    [Fact]
    public async Task Identical_seed_time_and_input_reproduce_an_identical_reading_sequence()
    {
        // Fix the lot ids so the two runs share identical input (not just structurally-equal targets).
        var targets = new[]
        {
            new TemperatureReadingTarget(new GelLotId(Guid.Parse("11111111-1111-1111-1111-111111111111")), new TemperatureRange(0m, 4m)),
            new TemperatureReadingTarget(new GelLotId(Guid.Parse("22222222-2222-2222-2222-222222222222")), new TemperatureRange(-5m, 5m)),
            new TemperatureReadingTarget(new GelLotId(Guid.Parse("33333333-3333-3333-3333-333333333333")), new TemperatureRange(10m, 10m)),
        };

        var first = new RecordingGateway();
        var second = new RecordingGateway();

        var span = TimeSpan.FromHours(3);
        var countA = await new TemperatureReadingGenerator(first, seed: 42).GenerateAsync(targets, Start, span);
        var countB = await new TemperatureReadingGenerator(second, seed: 42).GenerateAsync(targets, Start, span);

        Assert.Equal(countA, countB);
        Assert.Equal(first.Commands.Count, second.Commands.Count);

        for (var i = 0; i < first.Commands.Count; i++)
        {
            var a = first.Commands[i];
            var b = second.Commands[i];
            Assert.Equal(a.GelLotId, b.GelLotId);
            Assert.Equal(a.RecordedAt, b.RecordedAt);
            Assert.Equal(a.Celsius, b.Celsius); // bit-identical: same seeded draw
        }
    }

    [Fact]
    public async Task Different_seed_changes_the_reading_values()
    {
        var targets = new[]
        {
            new TemperatureReadingTarget(new GelLotId(Guid.Parse("44444444-4444-4444-4444-444444444444")), new TemperatureRange(-10m, 10m)),
        };

        var span = TimeSpan.FromHours(5);
        var runA = new RecordingGateway();
        var runB = new RecordingGateway();

        await new TemperatureReadingGenerator(runA, seed: 1).GenerateAsync(targets, Start, span);
        await new TemperatureReadingGenerator(runB, seed: 2).GenerateAsync(targets, Start, span);

        Assert.Equal(runA.Commands.Count, runB.Commands.Count);
        // Same count/timestamps, but at least one value differs under a different seed.
        var anyDifferent = false;
        for (var i = 0; i < runA.Commands.Count; i++)
        {
            Assert.Equal(runA.Commands[i].RecordedAt, runB.Commands[i].RecordedAt);
            if (runA.Commands[i].Celsius != runB.Commands[i].Celsius)
            {
                anyDifferent = true;
            }
        }

        Assert.True(anyDifferent, "A different seed should change at least one generated reading value.");
    }

    // A gateway that records every temperature command it receives and no-ops the rest. Only
    // RecordTemperatureReadingAsync is exercised by the generator.
    private sealed class RecordingGateway : IWarehouseCommandGateway
    {
        public List<RecordTemperatureReadingCommand> Commands { get; } = new();

        public Task<Result> RecordTemperatureReadingAsync(
            RecordTemperatureReadingCommand cmd, CancellationToken ct = default)
        {
            Commands.Add(cmd);
            return Task.FromResult(Result.Success());
        }

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(
            CreateColonyOrderCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> RecordInboundGelReceiptAsync(
            RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
