using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Simulation.Demand;

using Xunit;

namespace Forge.Tests.Simulation;

// Feature: nutrient-forge, Task 27.3 — ColonyDemandSimulator (Forge.Simulation).
//
// Covers the authoritative colony-demand generator's contract:
//   - trend boundaries change the active consumption rate for subsequent orders (Req 12.3)
//   - the operator demand multiplier scales generated demand (Req 20.6)
//   - an out-of-range demand profile is rejected naming the attribute (Req 12.6)
//   - a failed submission is retained and retried without generating duplicates (Req 12.5)
//   - identical profile + simulated time + seed reproduce identical orders (Req 12.7)
// The generator submits only through IWarehouseCommandGateway.CreateColonyOrderAsync (Req 12.4).
public sealed class ColonyDemandSimulatorTests
{
    private static readonly DateTimeOffset Start = new(2350, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly GelTypeId Gel = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static readonly ColonyId Colony = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    private static DemandProfile Profile(double baseRate, params TrendBoundary[] trends)
    {
        var result = DemandProfile.Create(
            new Dictionary<GelTypeId, double> { [Gel] = baseRate },
            trends);
        Assert.True(result.IsSuccess, "test profile should be valid");
        return result.Value;
    }

    private static ColonyDemandSource Source(DemandProfile profile) => new(Colony, profile);

    [Fact]
    public async Task Generates_orders_only_through_the_command_gateway()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 7);

        var result = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(3), demandMultiplier: 1.0);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value.SubmittedCount); // one order per whole hour
        Assert.Equal(3, gateway.Accepted.Count);
        Assert.All(gateway.Accepted, c => Assert.Equal(Colony, c.ColonyId));
    }

    [Fact]
    public async Task Trend_boundary_change_alters_the_active_consumption_rate()
    {
        // Baseline 100/hr, then a 3x surge boundary two hours in. Orders in the first two hours use
        // the baseline; orders from hour 2 onward follow the surged rate (Req 12.3).
        var boundary = TrendBoundary.Create(Start + TimeSpan.FromHours(2), multiplier: 3.0).Value;
        var profile = Profile(baseRate: 100, boundary);

        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 99);

        // Start sits exactly on an hour boundary, so span [Start, Start+4h) covers whole-hour windows
        // at +0h, +1h, +2h, +3h. The boundary at +2h means +0h/+1h use the baseline (rate 100) and
        // +2h/+3h follow the 3x surge (rate 300).
        await sim.GenerateAsync(new[] { Source(profile) }, Start, TimeSpan.FromHours(4), demandMultiplier: 1.0);

        var byWindow = gateway.Accepted
            .OrderBy(c => c.DeliveryWindowStart)
            .Select(c => c.Lines.Single(l => l.GelTypeId.Equals(Gel)).Quantity)
            .ToArray();

        Assert.Equal(4, byWindow.Length);
        var preBoundary = byWindow[0];   // window +0h, rate ~100
        var postBoundary = byWindow[3];  // window +3h, rate ~300

        Assert.True(
            postBoundary > preBoundary * 2,
            $"post-boundary quantity {postBoundary} should reflect the 3x surge over pre-boundary {preBoundary}");
    }

    [Fact]
    public async Task Demand_multiplier_scales_generated_output()
    {
        var profile = Profile(baseRate: 100);

        var low = new RecordingGateway();
        var high = new RecordingGateway();

        // Same seed + profile + time, only the operator demand multiplier differs (Req 20.6).
        await new ColonyDemandSimulator(low, seed: 5)
            .GenerateAsync(new[] { Source(profile) }, Start, TimeSpan.FromHours(4), demandMultiplier: 1.0);
        await new ColonyDemandSimulator(high, seed: 5)
            .GenerateAsync(new[] { Source(profile) }, Start, TimeSpan.FromHours(4), demandMultiplier: 4.0);

        var lowTotal = low.Accepted.Sum(c => c.Lines.Sum(l => l.Quantity));
        var highTotal = high.Accepted.Sum(c => c.Lines.Sum(l => l.Quantity));

        Assert.True(highTotal > lowTotal * 3, $"4x multiplier ({highTotal}) should far exceed 1x ({lowTotal})");
    }

    [Fact]
    public async Task Zero_multiplier_generates_no_orders()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 1);

        var result = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(5), demandMultiplier: 0.0);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.SubmittedCount);
        Assert.Empty(gateway.Accepted);
    }

    [Fact]
    public async Task Zero_delta_generates_no_orders()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 1);

        var result = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.Zero, demandMultiplier: 1.0);

        Assert.True(result.IsSuccess);
        Assert.Empty(gateway.Accepted);
    }

    [Fact]
    public async Task Negative_multiplier_is_rejected_naming_the_parameter()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 1);

        var result = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(1), demandMultiplier: -1.0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal("demandMultiplier", result.Error.Detail?["parameter"]);
        Assert.Empty(gateway.Accepted);
    }

    [Fact]
    public void Invalid_profile_is_rejected_naming_the_attribute()
    {
        // A negative base rate is outside the valid range (Req 12.6). ValidateProfile must name the
        // offending attribute (BaseRatePerHour). We build the invalid profile via reflection-free means:
        // DemandProfile.Create is the validator, so an out-of-range value fails there.
        var invalid = DemandProfile.Create(
            new Dictionary<GelTypeId, double> { [Gel] = -5.0 },
            Array.Empty<TrendBoundary>());

        Assert.True(invalid.IsFailure);
        Assert.Equal(ErrorKind.Validation, invalid.Error.Kind);
        Assert.Equal(nameof(DemandProfile.BaseRatePerHour), invalid.Error.Detail?["parameter"]);
    }

    [Fact]
    public async Task Invalid_multiplier_nan_is_rejected()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 1);

        var result = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(1), demandMultiplier: double.NaN);

        Assert.True(result.IsFailure);
        Assert.Equal("demandMultiplier", result.Error.Detail?["parameter"]);
    }

    [Fact]
    public async Task Failed_submission_is_retained_and_retried_without_duplicates()
    {
        // The gateway rejects the first attempt at each order, then accepts on retry (Req 12.5).
        var gateway = new FlakyGateway(failFirstAttempts: 1);
        var sim = new ColonyDemandSimulator(gateway, seed: 3);

        // First pass: every order's first submission fails, so all are retained as pending.
        var first = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(3), demandMultiplier: 1.0);

        Assert.True(first.IsSuccess);
        Assert.Equal(0, first.Value.SubmittedCount);
        Assert.Equal(3, first.Value.PendingCount);
        Assert.Equal(3, sim.PendingCount);
        Assert.Empty(gateway.Accepted); // nothing accepted yet

        // Second pass over the SAME span: pending orders are retried (now succeed) and no new
        // duplicates are generated for the already-covered windows.
        var second = await sim.GenerateAsync(
            new[] { Source(Profile(baseRate: 100)) }, Start, TimeSpan.FromHours(3), demandMultiplier: 1.0);

        Assert.True(second.IsSuccess);
        Assert.Equal(3, second.Value.SubmittedCount); // the 3 retried
        Assert.Equal(0, sim.PendingCount);

        // Exactly 3 distinct orders accepted total — no duplicates despite the retry.
        Assert.Equal(3, gateway.Accepted.Count);
        var distinctWindows = gateway.Accepted.Select(c => c.DeliveryWindowStart).Distinct().Count();
        Assert.Equal(3, distinctWindows);
    }

    [Fact]
    public async Task Repeated_generation_over_same_window_never_double_issues()
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, seed: 8);

        var source = new[] { Source(Profile(baseRate: 100)) };

        await sim.GenerateAsync(source, Start, TimeSpan.FromHours(3), demandMultiplier: 1.0);
        var acceptedAfterFirst = gateway.Accepted.Count;

        // Re-generate over the identical span — every window is already submitted, so nothing new.
        var again = await sim.GenerateAsync(source, Start, TimeSpan.FromHours(3), demandMultiplier: 1.0);

        Assert.Equal(0, again.Value.SubmittedCount);
        Assert.Equal(acceptedAfterFirst, gateway.Accepted.Count);
    }

    [Fact]
    public async Task Identical_profile_time_and_seed_reproduce_identical_orders()
    {
        var boundary = TrendBoundary.Create(Start + TimeSpan.FromHours(2), multiplier: 2.5).Value;
        var profile = Profile(baseRate: 120, boundary);

        var runA = new RecordingGateway();
        var runB = new RecordingGateway();

        var span = TimeSpan.FromHours(6);
        await new ColonyDemandSimulator(runA, seed: 2024)
            .GenerateAsync(new[] { Source(profile) }, Start, span, demandMultiplier: 1.5);
        await new ColonyDemandSimulator(runB, seed: 2024)
            .GenerateAsync(new[] { Source(profile) }, Start, span, demandMultiplier: 1.5);

        Assert.Equal(runA.Accepted.Count, runB.Accepted.Count);

        var ordered = (List<CreateColonyOrderCommand> a) =>
            a.OrderBy(c => c.DeliveryWindowStart).ToArray();
        var a = ordered(runA.Accepted);
        var b = ordered(runB.Accepted);

        for (var i = 0; i < a.Length; i++)
        {
            Assert.Equal(a[i].ColonyId, b[i].ColonyId);
            Assert.Equal(a[i].DeliveryWindowStart, b[i].DeliveryWindowStart);
            Assert.Equal(a[i].DeliveryWindowEnd, b[i].DeliveryWindowEnd);
            Assert.Equal(a[i].Lines.Count, b[i].Lines.Count);
            for (var j = 0; j < a[i].Lines.Count; j++)
            {
                Assert.Equal(a[i].Lines[j].GelTypeId, b[i].Lines[j].GelTypeId);
                Assert.Equal(a[i].Lines[j].Quantity, b[i].Lines[j].Quantity);
            }
        }
    }

    [Fact]
    public async Task Different_seed_can_change_generated_quantities()
    {
        var profile = Profile(baseRate: 100);

        var runA = new RecordingGateway();
        var runB = new RecordingGateway();

        var span = TimeSpan.FromHours(12);
        await new ColonyDemandSimulator(runA, seed: 1)
            .GenerateAsync(new[] { Source(profile) }, Start, span, demandMultiplier: 1.0);
        await new ColonyDemandSimulator(runB, seed: 2)
            .GenerateAsync(new[] { Source(profile) }, Start, span, demandMultiplier: 1.0);

        var a = runA.Accepted.OrderBy(c => c.DeliveryWindowStart)
            .Select(c => c.Lines.Single().Quantity).ToArray();
        var b = runB.Accepted.OrderBy(c => c.DeliveryWindowStart)
            .Select(c => c.Lines.Single().Quantity).ToArray();

        Assert.Equal(a.Length, b.Length);
        Assert.True(a.Zip(b).Any(p => p.First != p.Second),
            "a different seed should change at least one generated quantity across the span");
    }

    // A gateway that accepts and records every colony order. Other operations are unsupported.
    private sealed class RecordingGateway : IWarehouseCommandGateway
    {
        public List<CreateColonyOrderCommand> Accepted { get; } = new();

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(
            CreateColonyOrderCommand cmd, CancellationToken ct = default)
        {
            Accepted.Add(cmd);
            return Task.FromResult(Result.Success(ColonyOrderId.New()));
        }

        public Task<Result> RecordInboundGelReceiptAsync(
            RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> RecordTemperatureReadingAsync(
            RecordTemperatureReadingCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // A gateway that fails the first N submission attempts for each distinct order (keyed by the
    // colony + delivery window), then accepts. Records only orders it actually accepts, so a
    // duplicate submission would show up as an extra accepted entry.
    private sealed class FlakyGateway : IWarehouseCommandGateway
    {
        private readonly int _failFirstAttempts;
        private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public FlakyGateway(int failFirstAttempts) => _failFirstAttempts = failFirstAttempts;

        public List<CreateColonyOrderCommand> Accepted { get; } = new();

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(
            CreateColonyOrderCommand cmd, CancellationToken ct = default)
        {
            var key = $"{cmd.ColonyId.Value:N}:{cmd.DeliveryWindowStart:O}";
            var seen = _attempts.TryGetValue(key, out var n) ? n : 0;
            _attempts[key] = seen + 1;

            if (seen < _failFirstAttempts)
            {
                return Task.FromResult(
                    Result.Failure<ColonyOrderId>(DomainError.Validation("transient failure")));
            }

            Accepted.Add(cmd);
            return Task.FromResult(Result.Success(ColonyOrderId.New()));
        }

        public Task<Result> RecordInboundGelReceiptAsync(
            RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> RecordTemperatureReadingAsync(
            RecordTemperatureReadingCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
