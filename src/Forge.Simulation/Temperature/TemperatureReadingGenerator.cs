using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Simulation.Temperature;

/// <summary>
/// A single lot the <see cref="TemperatureReadingGenerator"/> produces readings for, paired with the
/// allowable temperature band of the zone the lot is assigned to (Req 6.1, 6.2). The generator centers
/// most readings inside <paramref name="ZoneRange"/> and occasionally drifts outside it so realistic
/// excursions can arise — but it never itself decides whether a reading is an excursion; that is core
/// domain logic (Req 6.3). The band is carried here (rather than a <c>ZoneId</c>) so the generator can
/// pick a plausible target temperature per lot without querying the core.
/// </summary>
/// <param name="LotId">The lot the generated readings pertain to.</param>
/// <param name="ZoneRange">The allowable temperature band of the lot's assigned zone.</param>
public readonly record struct TemperatureReadingTarget(GelLotId LotId, TemperatureRange ZoneRange);

/// <summary>
/// The Phase-1 Simulation driver's temperature-reading generator (design "Driver-generated inputs";
/// Req 6.2). Over a simulated time span it produces per-lot temperature readings from an explicitly
/// seeded PRNG stream and issues a <see cref="RecordTemperatureReadingCommand"/> to the WMS Core for
/// each, through <see cref="IWarehouseCommandGateway.RecordTemperatureReadingAsync"/>.
/// <para>
/// <b>Determinism (Req 6.6 spirit, design "Deterministic RNG").</b> Identical seed + identical
/// simulated start + identical simulated delta + identical ordered lot/zone input always produce an
/// identical sequence of readings (same count, values, order), so runs are reproducible. Determinism
/// is achieved by deriving one PRNG sub-stream per lot from <c>seed</c> and the lot's position in the
/// input, and by walking a fixed sample cadence across the delta. The generator holds no mutable state
/// between calls.
/// </para>
/// <para>
/// <b>Boundary of responsibility.</b> The generator only emits readings. It never inspects a reading
/// against the zone band to decide an excursion, never appends to lot history, and never mutates any
/// domain state — excursion detection and history ordering are core domain rules (Req 6.2, 6.3) applied
/// by the <c>RecordTemperatureReading</c> handler behind the command gateway.
/// </para>
/// </summary>
public sealed class TemperatureReadingGenerator
{
    /// <summary>The simulated interval between successive readings for a given lot.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Probability (per reading) that the generator lets the target drift beyond the zone band, so an
    /// excursion can plausibly occur. Kept low so most readings sit comfortably in range.
    /// </summary>
    private const double DriftProbability = 0.08;

    /// <summary>Fraction of the band's half-width the reading may wander within on an in-range sample.</summary>
    private const double InRangeJitterFraction = 0.9;

    /// <summary>Magnitude (in Celsius) by which a drifting reading pushes past the nearer band bound.</summary>
    private const double DriftMagnitudeCelsius = 3.0;

    private readonly IWarehouseCommandGateway _gateway;
    private readonly int _seed;

    /// <summary>
    /// Creates a generator that issues readings through <paramref name="gateway"/>, drawing from a PRNG
    /// stream rooted at <paramref name="seed"/> so the same seed reproduces the same readings.
    /// </summary>
    /// <param name="gateway">The core command entrypoint readings are issued through.</param>
    /// <param name="seed">The root seed for this generator's deterministic PRNG streams.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gateway"/> is null.</exception>
    public TemperatureReadingGenerator(IWarehouseCommandGateway gateway, int seed)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _seed = seed;
    }

    /// <summary>
    /// Generates and issues temperature readings for <paramref name="targets"/> across the simulated
    /// span <c>[start, start + simDelta)</c> (Req 6.2). For each lot, a reading is emitted at each
    /// <see cref="SampleInterval"/> boundary strictly inside the span; each is issued as a
    /// <see cref="RecordTemperatureReadingCommand"/> via the command gateway.
    /// <list type="bullet">
    ///   <item><description>
    ///     A non-positive <paramref name="simDelta"/> emits nothing (nothing to sample over — mirrors the
    ///     paused/zero-tick contract; Req 10.5). An empty <paramref name="targets"/> also emits nothing.
    ///   </description></item>
    ///   <item><description>
    ///     Lots are processed in the order supplied; each lot's readings are ordered by ascending
    ///     timestamp, so the overall issued sequence is fully determined by the inputs.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="targets">The lots (with their zone bands) to generate readings for, in a stable order.</param>
    /// <param name="start">The simulated instant the span begins at.</param>
    /// <param name="simDelta">The simulated duration to generate readings across.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of readings issued to the core.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    public async Task<int> GenerateAsync(
        IReadOnlyList<TemperatureReadingTarget> targets,
        DateTimeOffset start,
        TimeSpan simDelta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        if (simDelta <= TimeSpan.Zero || targets.Count == 0)
        {
            return 0;
        }

        var issued = 0;

        // One deterministic sub-stream per lot, keyed by seed + lot index, so the sequence is stable and
        // independent of how many lots precede it in absolute PRNG draws (Req 6.6 spirit / design RNG).
        for (var lotIndex = 0; lotIndex < targets.Count; lotIndex++)
        {
            var target = targets[lotIndex];
            var rng = new Random(TemperatureStream.SubStreamSeed(_seed, lotIndex));

            var offset = SampleInterval;
            while (offset < simDelta)
            {
                var celsius = NextReadingCelsius(rng, target.ZoneRange);
                var recordedAt = start + offset;

                var result = await _gateway
                    .RecordTemperatureReadingAsync(
                        new RecordTemperatureReadingCommand(target.LotId, celsius, recordedAt),
                        ct)
                    .ConfigureAwait(false);

                // The generator emits regardless of the core's accept/reject outcome (e.g. a zone-less lot
                // rejection, Req 6.4). It only counts what it issued; the core owns the outcome.
                _ = result;
                issued++;

                offset += SampleInterval;
            }
        }

        return issued;
    }

    /// <summary>
    /// Draws the next reading for a lot: mostly a value jittered within the zone band, occasionally a
    /// value pushed just past the nearer bound so an excursion can occur. Never classifies the value.
    /// </summary>
    private static double NextReadingCelsius(Random rng, TemperatureRange range)
    {
        var min = (double)range.MinCelsius;
        var max = (double)range.MaxCelsius;

        // Degenerate band (min == max): center on the point, drift a fixed magnitude off it.
        var mid = (min + max) / 2.0;
        var halfWidth = (max - min) / 2.0;

        if (rng.NextDouble() < DriftProbability)
        {
            // Push past whichever bound is nearer the drift direction so an out-of-range reading results.
            var high = rng.NextDouble() < 0.5;
            return high
                ? max + DriftMagnitudeCelsius * (rng.NextDouble() + 0.01)
                : min - DriftMagnitudeCelsius * (rng.NextDouble() + 0.01);
        }

        // In-range: jitter around the midpoint within a safe fraction of the half-width.
        var jitter = (rng.NextDouble() * 2.0 - 1.0) * halfWidth * InRangeJitterFraction;
        return mid + jitter;
    }
}

/// <summary>
/// Private-to-folder helper deriving stable per-lot PRNG sub-stream seeds for the temperature generator.
/// Kept internal to <c>Forge.Simulation.Temperature</c> to avoid a shared PRNG file (each simulation
/// concern owns its own seeded streams — design "Deterministic RNG").
/// </summary>
internal static class TemperatureStream
{
    /// <summary>
    /// Combines the generator's root <paramref name="seed"/> with a per-lot <paramref name="index"/> into
    /// a stable sub-stream seed. Uses an unchecked integer hash so the mapping is deterministic and
    /// overflow-safe across platforms.
    /// </summary>
    internal static int SubStreamSeed(int seed, int index)
    {
        unchecked
        {
            var hash = 17;
            hash = (hash * 31) + seed;
            hash = (hash * 31) + index;
            // A final mix so adjacent indices produce well-separated streams.
            hash ^= hash >> 15;
            hash *= unchecked((int)0x2c1b3c6d);
            hash ^= hash >> 12;
            return hash;
        }
    }
}
