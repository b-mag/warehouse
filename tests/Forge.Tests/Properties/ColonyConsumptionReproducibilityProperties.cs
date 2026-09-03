using CsCheck;
using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Simulation.Demand;
using Xunit;

namespace Forge.Tests.Properties;

/// <summary>
/// Property 15 — Colony consumption reproducibility (task 27.4), targeting
/// <see cref="ColonyDemandSimulator"/> in <c>Forge.Simulation</c>.
/// <para>
/// For ALL identical <see cref="DemandProfile"/>s, identical simulated time span, and identical
/// seed, two independently-constructed simulators run over the same colonies, the same simulated
/// span, and the same demand multiplier must issue byte-for-byte identical
/// <see cref="CreateColonyOrderCommand"/> sequences — same count, same order, same colony, same
/// delivery windows, and each line's gel type + quantity. Because generation is a pure function of
/// (profile, simulated time, seed), issue order itself is deterministic and is asserted directly
/// (no sort before comparison).
/// </para>
/// <para>**Validates: Requirements 12.7, 28.12**</para>
/// </summary>
public sealed class ColonyConsumptionReproducibilityProperties
{
    // ≥100 iterations required by the spec; set explicitly on Sample(..., iter: Iterations).
    private const int Iterations = 100;

    // A stable simulated-time anchor. All sampled spans start on/after this instant so trend
    // boundaries placed within the span are exercised across whole-hour windows.
    private static readonly DateTimeOffset Epoch = new(2350, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A single sampled reproducibility case: the shared inputs both simulators receive. Ids are
    /// fixed here (derived from stable tags) so the two runs genuinely see identical colonies and
    /// gel types — never <see cref="GelTypeId.New"/> / <see cref="ColonyId.New"/> independently per
    /// run.
    /// </summary>
    private sealed record Case(
        int Seed,
        DateTimeOffset SimStart,
        TimeSpan SimDelta,
        double DemandMultiplier,
        IReadOnlyList<ColonyDemandSource> Colonies);

    // A deterministic id from an int tag + a "kind" discriminator so gel-type ids and colony ids
    // never collide and a sampled case fully determines its colonies/gel types.
    private static Guid GuidFrom(int tag, byte kind)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, tag);
        bytes[15] = kind;
        return new Guid(bytes);
    }

    private static GelTypeId GelFrom(int tag) => new(GuidFrom(tag, kind: 0x0A));

    private static ColonyId ColonyFrom(int tag) => new(GuidFrom(tag, kind: 0x0C));

    // Base rate spanning "rounds below 1 unit/hr" through "clearly several units/hr".
    private static readonly Gen<double> GenBaseRate = Gen.Double[0.0, 500.0];

    // Trend multiplier: finite, non-negative — lulls (<1), no-op (1), surges (>1).
    private static readonly Gen<double> GenTrendMultiplier = Gen.Double[0.0, 5.0];

    // Operator demand multiplier: finite, non-negative (0 => no orders, still reproducible).
    private static readonly Gen<double> GenDemandMultiplier = Gen.Double[0.0, 4.0];

    // Seed spanning negative, zero, and positive — stream mixing must reproduce for any.
    private static readonly Gen<int> GenSeed = Gen.Int[-100_000, 100_000];

    // A full reproducibility case: 1..4 colonies, each with 1..3 gel types and 0..3 trend
    // boundaries placed within the sampled span. Every id is derived from a tag so both runs share
    // them. Span in whole hours 0..24 (0 exercises the "no generation" path). Start offset in
    // minutes keeps windows from always aligning to the epoch hour.
    private static readonly Gen<Case> GenCase =
        from seed in GenSeed
        from startOffsetMinutes in Gen.Int[0, 600]
        from spanHours in Gen.Int[0, 24]
        from demandMultiplier in GenDemandMultiplier
        from colonyCount in Gen.Int[1, 4]
        let simStart = Epoch + TimeSpan.FromMinutes(startOffsetMinutes)
        let simDelta = TimeSpan.FromHours(spanHours)
        from colonies in GenColonies(colonyCount, simStart, simStart + simDelta)
        select new Case(seed, simStart, simDelta, demandMultiplier, colonies);

    private static Gen<IReadOnlyList<ColonyDemandSource>> GenColonies(
        int colonyCount, DateTimeOffset simStart, DateTimeOffset spanEnd) =>
        GenColonyProfile(simStart, spanEnd)
            .Array[colonyCount, colonyCount]
            .Select(profiles =>
            {
                // Assign each colony a distinct, deterministic id keyed by its index so a case's
                // colonies are stable and shared across both runs.
                var sources = new ColonyDemandSource[profiles.Length];
                for (var i = 0; i < profiles.Length; i++)
                {
                    sources[i] = new ColonyDemandSource(ColonyFrom(i + 1), profiles[i]);
                }

                return (IReadOnlyList<ColonyDemandSource>)sources;
            });

    private static Gen<DemandProfile> GenColonyProfile(
        DateTimeOffset simStart, DateTimeOffset spanEnd) =>
        from gelCount in Gen.Int[1, 3]
        from rates in GenBaseRate.Array[gelCount, gelCount]
        from trends in GenTrends(simStart, spanEnd)
        select DemandProfile.Create(BaseRateMap(rates), trends).Value;

    private static IReadOnlyDictionary<GelTypeId, double> BaseRateMap(double[] rates)
    {
        var map = new Dictionary<GelTypeId, double>(rates.Length);
        for (var i = 0; i < rates.Length; i++)
        {
            map[GelFrom(i + 1)] = rates[i];
        }

        return map;
    }

    private static Gen<IReadOnlyList<TrendBoundary>> GenTrends(
        DateTimeOffset simStart, DateTimeOffset spanEnd)
    {
        // Place boundaries at minute offsets within [start, end) so they actually change the active
        // rate for some windows. A zero span degenerates to boundaries at the start.
        var spanMinutes = Math.Max(1, (int)(spanEnd - simStart).TotalMinutes);

        return
            from trendCount in Gen.Int[0, 3]
            from offsets in Gen.Int[0, spanMinutes].Array[trendCount, trendCount]
            from multipliers in GenTrendMultiplier.Array[trendCount, trendCount]
            select BuildTrends(simStart, offsets, multipliers);
    }

    private static IReadOnlyList<TrendBoundary> BuildTrends(
        DateTimeOffset simStart, int[] offsets, double[] multipliers)
    {
        var boundaries = new TrendBoundary[offsets.Length];
        for (var i = 0; i < offsets.Length; i++)
        {
            boundaries[i] = TrendBoundary
                .Create(simStart + TimeSpan.FromMinutes(offsets[i]), multipliers[i])
                .Value;
        }

        return boundaries;
    }

    /// <summary>
    /// Two independently-constructed simulators sharing the same seed, run over the same colonies,
    /// the same simulated span, and the same demand multiplier, issue identical command sequences.
    /// <para>**Validates: Requirements 12.7, 28.12**</para>
    /// </summary>
    [Fact]
    public void IdenticalProfileTimeAndSeed_reproduceIdenticalOrders()
    {
        GenCase.Sample(
            testCase =>
            {
                var runA = Run(testCase);
                var runB = Run(testCase);

                Assert.Equal(runA.Count, runB.Count);
                for (var i = 0; i < runA.Count; i++)
                {
                    var a = runA[i];
                    var b = runB[i];

                    Assert.Equal(a.ColonyId, b.ColonyId);
                    Assert.Equal(a.DeliveryWindowStart, b.DeliveryWindowStart);
                    Assert.Equal(a.DeliveryWindowEnd, b.DeliveryWindowEnd);
                    Assert.Equal(a.Lines.Count, b.Lines.Count);
                    for (var j = 0; j < a.Lines.Count; j++)
                    {
                        Assert.Equal(a.Lines[j].GelTypeId, b.Lines[j].GelTypeId);
                        Assert.Equal(a.Lines[j].Quantity, b.Lines[j].Quantity);
                    }
                }
            },
            iter: Iterations);
    }

    // Run a case through a fresh simulator + recording gateway, returning the issued commands in the
    // exact order the simulator issued them (issue order is the property under test).
    private static IReadOnlyList<CreateColonyOrderCommand> Run(Case testCase)
    {
        var gateway = new RecordingGateway();
        var sim = new ColonyDemandSimulator(gateway, testCase.Seed);

        var result = sim.GenerateAsync(
                testCase.Colonies,
                testCase.SimStart,
                testCase.SimDelta,
                testCase.DemandMultiplier)
            .GetAwaiter().GetResult();

        Assert.True(result.IsSuccess, "a valid case should generate successfully");
        return gateway.Accepted;
    }

    // Accepts and records every colony order in issue order; other operations are unsupported.
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
}
