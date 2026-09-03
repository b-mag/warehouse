namespace Forge.Simulation.Demand;

using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Domain.Colonies;
using Forge.Domain.Common;

/// <summary>
/// A colony whose demand this simulator generates: an identity plus its validated
/// <see cref="DemandProfile"/> (Req 12.2). This mirrors <see cref="Colony"/> but keeps the
/// simulator decoupled from the domain aggregate — the simulator only needs the id + profile
/// pair to evolve consumption and issue orders.
/// </summary>
/// <param name="ColonyId">The colony placing orders.</param>
/// <param name="Profile">The colony's demand shape (base rates + trend boundaries).</param>
public sealed record ColonyDemandSource(ColonyId ColonyId, DemandProfile Profile);

/// <summary>
/// The authoritative colony-demand generator, resident in the Simulation input driver and moved
/// <b>out of the Game</b> (Req 2.4, 12.2, and the design's "colony demand placement" note). It is
/// the only producer of authoritative <see cref="CreateColonyOrderCommand"/>s: reading each
/// colony's <see cref="DemandProfile"/> and drawing from deterministic seeded streams, it evolves
/// consumption across trend boundaries (Req 12.3), scales the result by the operator's demand
/// multiplier (Req 20.6), and submits orders to the WMS Core only through
/// <see cref="IWarehouseCommandGateway.CreateColonyOrderAsync"/> (Req 12.4).
/// <para>
/// <b>Determinism (Req 12.7 — Property 15).</b> Order generation is a pure function of
/// <c>(profile, simulated time, seed)</c>. Per colony and per generation window the simulator
/// derives a stream key from the seed, the colony id, and the whole-hour window index of the
/// simulated time, so identical inputs always yield an identical <see cref="CreateColonyOrderCommand"/>.
/// No wall-clock, no <see cref="Guid.NewGuid"/>, no ambient state feeds the draw.
/// </para>
/// <para>
/// <b>Validation (Req 12.6).</b> Before generating, a profile is re-validated through
/// <see cref="DemandProfile.Create"/> (the single source of the attribute range rules). An invalid
/// profile is rejected with a <see cref="DomainError.Validation"/> that names the offending
/// attribute; the simulator generates nothing for it.
/// </para>
/// <para>
/// <b>Retry without duplicates (Req 12.5).</b> Each generated order carries a deterministic
/// identity key. A submission that fails is retained in a pending buffer and retried on the next
/// <see cref="GenerateAsync"/> call before any new generation; the same key is never issued twice,
/// so a retry can never double-order.
/// </para>
/// </summary>
public sealed class ColonyDemandSimulator
{
    // One generated order per colony per whole simulated hour. This fixed cadence is what makes the
    // "window index" a stable, discretized function of simulated time (Req 12.7).
    private static readonly TimeSpan OrderWindow = TimeSpan.FromHours(1);

    private readonly IWarehouseCommandGateway _gateway;
    private readonly int _seed;

    // Deterministic identity keys of orders already accepted by the core — used so a retry never
    // re-issues an order the core already took (Req 12.5).
    private readonly HashSet<string> _submitted = new(StringComparer.Ordinal);

    // Orders generated but not yet accepted by the core (submission failed). Retained in order and
    // retried before new generation (Req 12.5). Keyed the same way as _submitted.
    private readonly List<PendingOrder> _pending = new();

    /// <summary>
    /// Create a simulator that submits through <paramref name="gateway"/> and draws all stochastic
    /// choices from streams derived from <paramref name="seed"/> (Req 12.4, 12.7).
    /// </summary>
    public ColonyDemandSimulator(IWarehouseCommandGateway gateway, int seed)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _seed = seed;
    }

    /// <summary>The count of generated orders still awaiting successful submission (Req 12.5).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Validate a demand profile's attributes against their valid ranges (Req 12.6). Delegates to
    /// <see cref="DemandProfile.Create"/> so the range rules live in one place; on failure the
    /// returned error names the offending attribute (e.g. <c>BaseRatePerHour</c> or
    /// <c>Multiplier</c>). Returns the (normalized) profile on success.
    /// </summary>
    public static Result<DemandProfile> ValidateProfile(DemandProfile profile)
    {
        if (profile is null)
        {
            return DomainError.Validation("Demand profile is required.", nameof(profile));
        }

        // Re-run the domain factory so a profile assembled from out-of-range attributes is rejected
        // with the attribute named, exactly as the domain would reject it on load.
        return DemandProfile.Create(profile.BaseRatePerHour, profile.Trends);
    }

    /// <summary>
    /// Generate authoritative colony orders for the simulated span
    /// <c>[<paramref name="simTimeStart"/>, simTimeStart + <paramref name="simDelta"/>)</c> and submit
    /// them to the core (Req 12.2, 12.4). For each whole simulated hour that the span crosses, each
    /// colony emits one order whose lines are its per-gel-type consumption over that hour — the
    /// profile's effective <see cref="DemandProfile.RateAt"/> (which already folds in the active trend
    /// boundary, Req 12.3) scaled by <paramref name="demandMultiplier"/> (Req 20.6) and by a small,
    /// deterministic seeded jitter. Lines rounding to a quantity below 1 are dropped; an order with no
    /// remaining lines is not issued.
    /// <para>
    /// Before generating, any orders left pending from earlier failed submissions are retried
    /// (Req 12.5). A submission failure retains the order in the pending buffer for the next call and
    /// never re-issues an already-accepted order.
    /// </para>
    /// </summary>
    /// <param name="colonies">The colonies to generate demand for (each with its profile).</param>
    /// <param name="simTimeStart">The simulated time at the start of this generation span.</param>
    /// <param name="simDelta">The simulated span length; zero or negative generates nothing.</param>
    /// <param name="demandMultiplier">
    /// The operator's colony demand multiplier (Req 20.6). Must be finite and non-negative; an invalid
    /// value is rejected naming the parameter and nothing is generated.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A summary of how many orders were submitted this call and how many remain pending, or a
    /// validation failure if an argument is out of range.
    /// </returns>
    public async Task<Result<DemandGenerationResult>> GenerateAsync(
        IReadOnlyList<ColonyDemandSource> colonies,
        DateTimeOffset simTimeStart,
        TimeSpan simDelta,
        double demandMultiplier,
        CancellationToken ct = default)
    {
        if (colonies is null)
        {
            return DomainError.Validation("Colonies are required.", nameof(colonies));
        }

        if (double.IsNaN(demandMultiplier) || double.IsInfinity(demandMultiplier))
        {
            return DomainError.Validation(
                $"Demand multiplier must be a finite number but was {demandMultiplier}.",
                nameof(demandMultiplier));
        }

        if (demandMultiplier < 0)
        {
            return DomainError.Validation(
                $"Demand multiplier must be non-negative but was {demandMultiplier}.",
                nameof(demandMultiplier));
        }

        // Validate every profile up front (Req 12.6). Reject the whole call naming the attribute so a
        // misconfigured profile never silently produces (or skips) orders.
        foreach (var colony in colonies)
        {
            var validated = ValidateProfile(colony.Profile);
            if (validated.IsFailure)
            {
                return Result<DemandGenerationResult>.Failure(validated.Error);
            }
        }

        var submitted = 0;

        // 1) Retry anything left pending from earlier failed submissions (Req 12.5), oldest first.
        submitted += await FlushPendingAsync(ct).ConfigureAwait(false);

        // 2) Generate new orders for each whole simulated hour the span crosses.
        if (simDelta > TimeSpan.Zero)
        {
            foreach (var order in EnumerateWindowOrders(colonies, simTimeStart, simDelta, demandMultiplier))
            {
                if (_submitted.Contains(order.Key))
                {
                    // Already accepted by the core in an earlier call — never double-issue (Req 12.5).
                    continue;
                }

                submitted += await SubmitAsync(order, ct).ConfigureAwait(false);
            }
        }

        return new DemandGenerationResult(submitted, _pending.Count);
    }

    // Retry pending orders in FIFO order. Successfully accepted orders leave the buffer; a still-
    // failing order is retained (and generation for this call stops re-trying it once it fails again,
    // to avoid hammering the core within a single tick). Returns the number newly accepted.
    private async Task<int> FlushPendingAsync(CancellationToken ct)
    {
        if (_pending.Count == 0)
        {
            return 0;
        }

        var accepted = 0;
        var stillPending = new List<PendingOrder>(_pending.Count);

        foreach (var order in _pending)
        {
            if (_submitted.Contains(order.Key))
            {
                // Defensive: already accepted; drop it.
                continue;
            }

            var result = await _gateway.CreateColonyOrderAsync(order.Command, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _submitted.Add(order.Key);
                accepted++;
            }
            else
            {
                stillPending.Add(order);
            }
        }

        _pending.Clear();
        _pending.AddRange(stillPending);
        return accepted;
    }

    // Submit a freshly generated order. On success record its key; on failure retain it for retry
    // (Req 12.5). Returns 1 when accepted, 0 otherwise.
    private async Task<int> SubmitAsync(PendingOrder order, CancellationToken ct)
    {
        var result = await _gateway.CreateColonyOrderAsync(order.Command, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _submitted.Add(order.Key);
            return 1;
        }

        _pending.Add(order);
        return 0;
    }

    // Produce one order per colony per whole simulated hour that the span [start, start+delta)
    // crosses. A "window" is the whole-hour bucket a boundary falls in; its index is a pure function
    // of simulated time, which (with the seed and colony id) makes each order deterministic (Req 12.7).
    private IEnumerable<PendingOrder> EnumerateWindowOrders(
        IReadOnlyList<ColonyDemandSource> colonies,
        DateTimeOffset simTimeStart,
        TimeSpan simDelta,
        double demandMultiplier)
    {
        var spanEnd = simTimeStart + simDelta;

        // The index of the first whole-hour window boundary at or after the span start.
        var firstWindow = (long)Math.Ceiling(simTimeStart.Ticks / (double)OrderWindow.Ticks);

        for (var window = firstWindow; ; window++)
        {
            var windowStart = new DateTimeOffset(window * OrderWindow.Ticks, TimeSpan.Zero);
            if (windowStart >= spanEnd)
            {
                yield break;
            }

            if (windowStart < simTimeStart)
            {
                continue;
            }

            foreach (var colony in colonies)
            {
                var order = BuildOrder(colony, windowStart, window, demandMultiplier);
                if (order is not null)
                {
                    yield return order;
                }
            }
        }
    }

    // Build the order a single colony places for a single whole-hour window. Pure function of
    // (profile, windowStart, window index, seed, demandMultiplier):
    //   quantity(gelType) = round( RateAt(gelType, windowStart) * demandMultiplier * jitter * 1h )
    // where jitter is a small deterministic seeded factor derived from (seed, colonyId, window).
    private PendingOrder? BuildOrder(
        ColonyDemandSource colony,
        DateTimeOffset windowStart,
        long window,
        double demandMultiplier)
    {
        var stream = new DeterministicStream(_seed, colony.ColonyId, window);

        // Gel types are enumerated in a stable (sorted) order so the seeded draws are consumed in a
        // reproducible sequence regardless of the profile dictionary's internal ordering.
        var gelTypes = colony.Profile.BaseRatePerHour.Keys.OrderBy(g => g).ToArray();

        var lines = new List<ColonyOrderLine>(gelTypes.Length);
        foreach (var gelType in gelTypes)
        {
            // Draw the per-line jitter regardless of the rate so the draw sequence stays aligned
            // across profiles and multipliers (the trend/multiplier only scales the expected value).
            var jitter = 0.85 + (stream.NextUnit() * 0.30);

            // RateAt already folds in the active trend boundary's multiplier (Req 12.3); the operator
            // demand multiplier scales that further (Req 20.6).
            var ratePerHour = colony.Profile.RateAt(gelType, windowStart);
            if (ratePerHour <= 0)
            {
                continue;
            }

            var expected = ratePerHour * jitter * demandMultiplier;
            var quantity = (int)Math.Round(expected, MidpointRounding.AwayFromZero);
            if (quantity >= 1)
            {
                lines.Add(new ColonyOrderLine(gelType, quantity));
            }
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var command = new CreateColonyOrderCommand(
            colony.ColonyId,
            lines,
            DeliveryWindowStart: windowStart,
            DeliveryWindowEnd: windowStart + OrderWindow);

        // Deterministic identity: colony + window uniquely identify one authoritative order, so a
        // retry of the same (colony, window) is recognized as the same order and never duplicated.
        var key = $"{colony.ColonyId.Value:N}:{window}";
        return new PendingOrder(key, command);
    }

    // A generated-but-not-necessarily-submitted order plus its deterministic identity key.
    private sealed record PendingOrder(string Key, CreateColonyOrderCommand Command);

    /// <summary>
    /// A tiny deterministic value stream, private to this folder (no shared PRNG file — conflict
    /// risk with sibling generators). It seeds a <see cref="Random"/> purely from
    /// <c>(seed, colonyId, window)</c> so two simulators built with the same seed produce identical
    /// draws for the same colony/window (Req 12.7). <see cref="NextUnit"/> yields a value in [0, 1).
    /// </summary>
    private sealed class DeterministicStream
    {
        private readonly Random _random;

        public DeterministicStream(int seed, ColonyId colony, long window)
        {
            // Combine the inputs into a single 32-bit seed via a stable hash. HashCode.Combine over the
            // same inputs is deterministic within a process; to be robust across processes we fold the
            // colony Guid + window explicitly rather than relying on Guid.GetHashCode.
            var colonyHash = FoldGuid(colony.Value);
            var mixed = unchecked(seed * 397) ^ colonyHash;
            mixed = unchecked(mixed * 397) ^ (int)(window & 0xFFFFFFFF);
            mixed = unchecked(mixed * 397) ^ (int)((window >> 32) & 0xFFFFFFFF);
            _random = new Random(mixed);
        }

        public double NextUnit() => _random.NextDouble();

        private static int FoldGuid(Guid value)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            var acc = 17;
            foreach (var b in bytes)
            {
                acc = unchecked((acc * 31) + b);
            }

            return acc;
        }
    }
}

/// <summary>
/// The outcome of a <see cref="ColonyDemandSimulator.GenerateAsync"/> pass: how many orders the core
/// accepted this call and how many remain pending (retained for retry — Req 12.5).
/// </summary>
/// <param name="SubmittedCount">Orders accepted by the core during this call (new + successful retries).</param>
/// <param name="PendingCount">Orders still awaiting successful submission after this call.</param>
public sealed record DemandGenerationResult(int SubmittedCount, int PendingCount);
