using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Domain.Common;

namespace Forge.Simulation.Arrivals;

/// <summary>
/// Deterministic, seeded generator of inbound gel arrivals (design "Driver-generated inputs";
/// Req 11.1, 14.1, 20.5). Living only in <c>Forge.Simulation</c> (the input driver), it turns the
/// passage of simulated time into a stream of <see cref="RecordInboundGelReceiptCommand"/>s issued
/// to the WMS Core through <see cref="IWarehouseCommandGateway.RecordInboundGelReceiptAsync"/>. The
/// core never generates these itself; it only receives them at a dock bay and issues a put-away
/// task via slotting.
/// <para>
/// <b>Rate (Req 20.5).</b> Arrivals are produced at the current inbound arrival rate, exposed as the
/// operator-adjustable <see cref="ArrivalRatePerHour"/>. Changing it takes effect for subsequent
/// simulated time. A zero (or non-positive) rate produces no arrivals.
/// </para>
/// <para>
/// <b>Determinism (critical).</b> The generator draws from a PRNG stream that is a pure function of
/// its base <c>seed</c> and the <i>simulated time window</i> being generated (the window's start
/// tick), never from wall-clock time or ambient <see cref="Guid.NewGuid"/>. Consequently an
/// identical <c>seed</c> + identical simulated window + identical rate always yields an identical
/// sequence of commands: same count, same values, same order. Gel types and dock bays are selected
/// deterministically from that same stream out of the supplied catalogs (which the caller keeps in a
/// stable, e.g. ascending-id, order).
/// </para>
/// </summary>
public sealed class ArrivalGenerator
{
    private readonly IWarehouseCommandGateway _gateway;
    private readonly ulong _seed;
    private readonly IReadOnlyList<GelTypeId> _gelTypes;
    private readonly IReadOnlyList<DockBayId> _dockBays;
    private readonly int _minQuantity;
    private readonly int _maxQuantity;
    private readonly TimeSpan _maxProductionAge;

    /// <summary>
    /// Current inbound arrival rate in arrivals per simulated hour (Operator_Parameter, Req 20.5).
    /// Operator changes assign this; subsequent <see cref="GenerateAsync"/> windows use the new
    /// value. Must be a non-negative, finite number; a zero rate produces no arrivals.
    /// </summary>
    public double ArrivalRatePerHour { get; set; }

    /// <summary>
    /// Create an arrival generator.
    /// </summary>
    /// <param name="gateway">The core command entrypoint arrivals are submitted through.</param>
    /// <param name="seed">Base PRNG seed for this generator's own stream (kept separate per concern).</param>
    /// <param name="gelTypes">Catalog of gel types to draw arrivals from (stable order; non-empty).</param>
    /// <param name="dockBays">Catalog of dock bays arrivals are received at (stable order; non-empty).</param>
    /// <param name="initialArrivalRatePerHour">Initial inbound arrival rate (arrivals/simulated hour); must be &gt;= 0.</param>
    /// <param name="minQuantity">Minimum received quantity per arrival (&gt;= 1).</param>
    /// <param name="maxQuantity">Maximum received quantity per arrival (&gt;= <paramref name="minQuantity"/>).</param>
    /// <param name="maxProductionAge">
    /// Upper bound on how far in the past a received lot was produced, relative to the window end
    /// (drives <c>ProducedAt</c>; the core derives expiry from the formulation's nominal shelf-life).
    /// </param>
    public ArrivalGenerator(
        IWarehouseCommandGateway gateway,
        ulong seed,
        IReadOnlyList<GelTypeId> gelTypes,
        IReadOnlyList<DockBayId> dockBays,
        double initialArrivalRatePerHour = 0.0,
        int minQuantity = 1,
        int maxQuantity = 100,
        TimeSpan? maxProductionAge = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(gelTypes);
        ArgumentNullException.ThrowIfNull(dockBays);
        if (gelTypes.Count == 0)
            throw new ArgumentException("At least one gel type is required to generate arrivals.", nameof(gelTypes));
        if (dockBays.Count == 0)
            throw new ArgumentException("At least one dock bay is required to generate arrivals.", nameof(dockBays));
        if (!double.IsFinite(initialArrivalRatePerHour) || initialArrivalRatePerHour < 0.0)
            throw new ArgumentOutOfRangeException(nameof(initialArrivalRatePerHour), "Arrival rate must be a non-negative, finite value.");
        if (minQuantity < 1)
            throw new ArgumentOutOfRangeException(nameof(minQuantity), "Minimum quantity must be >= 1.");
        if (maxQuantity < minQuantity)
            throw new ArgumentOutOfRangeException(nameof(maxQuantity), "Maximum quantity must be >= minimum quantity.");

        _gateway = gateway;
        _seed = seed;
        // Snapshot the catalogs so the sequence cannot shift under the generator mid-window.
        _gelTypes = [.. gelTypes];
        _dockBays = [.. dockBays];
        _minQuantity = minQuantity;
        _maxQuantity = maxQuantity;
        _maxProductionAge = maxProductionAge ?? TimeSpan.FromDays(1);
        ArrivalRatePerHour = initialArrivalRatePerHour;
    }

    /// <summary>
    /// Generate the inbound arrivals for the simulated window <c>[windowStart, windowStart + simDelta)</c>
    /// at the current <see cref="ArrivalRatePerHour"/> and issue a
    /// <see cref="RecordInboundGelReceiptCommand"/> to the core for each (Req 11.1, 14.1).
    /// <para>
    /// The PRNG stream is derived purely from the base seed and <paramref name="windowStart"/>, so
    /// repeated calls with an identical seed + identical <paramref name="windowStart"/> + identical
    /// <paramref name="simDelta"/> + identical rate emit an identical command sequence. A non-positive
    /// <paramref name="simDelta"/> or a zero rate produces no arrivals.
    /// </para>
    /// </summary>
    /// <param name="windowStart">The simulated time at the start of the window being generated.</param>
    /// <param name="simDelta">The simulated time span the window covers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The commands issued this window, in the order they were issued.</returns>
    public async Task<IReadOnlyList<RecordInboundGelReceiptCommand>> GenerateAsync(
        DateTimeOffset windowStart,
        TimeSpan simDelta,
        CancellationToken ct = default)
    {
        var commands = BuildCommands(windowStart, simDelta);
        foreach (var cmd in commands)
        {
            ct.ThrowIfCancellationRequested();
            await _gateway.RecordInboundGelReceiptAsync(cmd, ct).ConfigureAwait(false);
        }

        return commands;
    }

    /// <summary>
    /// Pure command construction for the given window: identical inputs (seed, window start, delta,
    /// rate, catalogs) always produce an identical, ordered list. Exposed for reproducibility tests
    /// and reused by <see cref="GenerateAsync"/> so what is tested is exactly what is issued.
    /// </summary>
    public IReadOnlyList<RecordInboundGelReceiptCommand> BuildCommands(DateTimeOffset windowStart, TimeSpan simDelta)
    {
        if (simDelta <= TimeSpan.Zero || ArrivalRatePerHour <= 0.0 || !double.IsFinite(ArrivalRatePerHour))
            return [];

        // The stream is a pure function of (seed, window start) — never wall clock or Guid.NewGuid.
        var rng = new Rng(Mix(_seed, unchecked((ulong)windowStart.UtcTicks)));

        double hours = simDelta.TotalHours;
        double expected = ArrivalRatePerHour * hours;
        int count = SamplePoisson(ref rng, expected);
        if (count == 0)
            return [];

        long windowTicks = simDelta.Ticks;
        long maxAgeTicks = _maxProductionAge.Ticks;

        var commands = new List<RecordInboundGelReceiptCommand>(count);
        for (int i = 0; i < count; i++)
        {
            var gelTypeId = _gelTypes[rng.NextInt(_gelTypes.Count)];
            var dockBayId = _dockBays[rng.NextInt(_dockBays.Count)];

            int quantity = _minQuantity == _maxQuantity
                ? _minQuantity
                : _minQuantity + rng.NextInt(_maxQuantity - _minQuantity + 1);

            // Arrival instant within the window, and a production time some bounded age before it.
            long offsetTicks = windowTicks <= 0 ? 0 : (long)(rng.NextDouble() * windowTicks);
            var arrivalAt = windowStart + TimeSpan.FromTicks(offsetTicks);
            long ageTicks = maxAgeTicks <= 0 ? 0 : (long)(rng.NextDouble() * maxAgeTicks);
            var producedAt = arrivalAt - TimeSpan.FromTicks(ageTicks);

            commands.Add(new RecordInboundGelReceiptCommand(gelTypeId, producedAt, quantity, dockBayId));
        }

        return commands;
    }

    /// <summary>
    /// Draw a Poisson-distributed count for the given mean using Knuth's method over the seeded
    /// stream. Deterministic in the stream, so identical stream state + identical mean yields an
    /// identical count. Guards a very large mean to keep the loop bounded.
    /// </summary>
    private static int SamplePoisson(ref Rng rng, double mean)
    {
        if (mean <= 0.0)
            return 0;

        // Knuth is fine for the small means an arrival rate produces per tick. Cap to stay bounded.
        const double meanCap = 1_000_000.0;
        if (mean > meanCap)
            mean = meanCap;

        double limit = Math.Exp(-mean);
        double product = 1.0;
        int k = 0;
        do
        {
            k++;
            product *= rng.NextDouble();
        }
        while (product > limit);

        return k - 1;
    }

    /// <summary>
    /// Combine the base seed with the window key into a well-mixed stream seed so consecutive windows
    /// (which differ by a small tick delta) produce well-separated, non-correlated streams.
    /// SplitMix64 finalizer.
    /// </summary>
    private static ulong Mix(ulong seed, ulong windowKey)
    {
        ulong z = unchecked(seed ^ (windowKey + 0x9E3779B97F4A7C15UL));
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    /// <summary>
    /// A tiny SplitMix64 PRNG kept private to this file (per the driver's "one stream per concern"
    /// rule) so it never becomes a shared helper that could collide with sibling generators. Pure and
    /// deterministic: identical initial state yields an identical value sequence.
    /// </summary>
    private struct Rng(ulong state)
    {
        private ulong _state = state;

        private ulong NextUInt64()
        {
            _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
            ulong z = _state;
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            return z ^ (z >> 31);
        }

        /// <summary>A double in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>A non-negative int in [0, exclusiveUpper). Requires exclusiveUpper &gt; 0.</summary>
        public int NextInt(int exclusiveUpper) => (int)(NextDouble() * exclusiveUpper);
    }
}
