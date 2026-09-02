using CsCheck;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 2: Expiry-decay consistency and single-transition event
//
// Property 2 (design.md): "For any gel lot, any current time, and any non-negative whole-second
// delta, advancing the clock by the delta and then computing remaining shelf-life SHALL equal
// computing remaining shelf-life directly at now + delta; the lot SHALL be marked expired exactly
// when its remaining whole-second shelf-life is at or below zero; already-expired lots SHALL remain
// unchanged; and crossing the expiry boundary SHALL raise exactly one expiry event regardless of how
// the interval is subdivided."
//
// Validates: Requirements 4.1, 4.2, 4.3, 4.4, 4.6, 4.7, 28.6
public sealed class ExpiryDecayProperties
{
    // ≥100 iterations required by the spec; set explicitly on every Sample(..., iter: Iterations).
    private const int Iterations = 100;

    // A fixed simulated epoch to anchor generated timestamps (year 2200 like the rest of the suite).
    private static readonly DateTimeOffset Epoch = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Whole-second offsets, in seconds, within roughly ±/+ ten years. Kept in whole seconds so the
    // "measured in whole seconds" semantics (Req 4.1) are exercised without sub-second noise.
    private const long TenYearsSeconds = 10L * 365 * 24 * 60 * 60;

    /// <summary>Produced-at instants: Epoch plus a whole-second offset in [-10y, +10y].</summary>
    private static readonly Gen<DateTimeOffset> GenProducedAt =
        Gen.Long[-TenYearsSeconds, TenYearsSeconds].Select(s => Epoch.AddSeconds(s));

    /// <summary>Nominal shelf-life: 1 second .. 365 days of whole seconds (Req 25.1 bucket range).</summary>
    private static readonly Gen<TimeSpan> GenShelfLife =
        Gen.Long[1, 365L * 24 * 60 * 60].Select(TimeSpan.FromSeconds);

    /// <summary>Evaluation instants ("now"): Epoch plus a whole-second offset in [-10y, +10y].</summary>
    private static readonly Gen<DateTimeOffset> GenNow =
        Gen.Long[-TenYearsSeconds, TenYearsSeconds].Select(s => Epoch.AddSeconds(s));

    /// <summary>Non-negative whole-second delta: 0 .. 20 years of seconds.</summary>
    private static readonly Gen<long> GenNonNegativeDeltaSeconds =
        Gen.Long[0, 2 * TenYearsSeconds];

    private static readonly TemperatureRange AnyRange = new(2m, 8m);
    private static readonly IReadOnlyList<string> AnyFlavors = new[] { "vanilla" };

    private static GelLot NewLot(DateTimeOffset producedAt, TimeSpan shelfLife)
    {
        var formulation = new Formulation(AnyRange, shelfLife, AnyFlavors);
        return GelLot.Create(GelLotId.New(), GelTypeId.New(), formulation, producedAt, quantity: 10);
    }

    // Req 4.1 / 4.7: advancing the clock by delta then evaluating equals evaluating directly at
    // now + delta. Because ExpiresAt is fixed at creation, "advancing" is recomputation against the
    // later instant; the two must agree exactly for both the raw span and the whole-second count.
    [Fact]
    public void AdvancingThenEvaluating_EqualsEvaluatingAtNowPlusDelta()
    {
        Gen.Select(GenProducedAt, GenShelfLife, GenNow, GenNonNegativeDeltaSeconds)
            .Sample((producedAt, shelfLife, now, deltaSeconds) =>
            {
                var lot = NewLot(producedAt, shelfLife);
                var later = now.AddSeconds(deltaSeconds);

                var advancedSpan = lot.RemainingShelfLife(later);
                var directSpan = lot.RemainingShelfLife(now + TimeSpan.FromSeconds(deltaSeconds));

                var advancedSeconds = lot.RemainingWholeSeconds(later);
                var directSeconds = lot.RemainingWholeSeconds(now + TimeSpan.FromSeconds(deltaSeconds));

                return advancedSpan == directSpan && advancedSeconds == directSeconds;
            }, iter: Iterations);
    }

    // Req 4.3: a fresh lot is expired (TryExpireAt transitions, IsExpired) exactly when its remaining
    // whole-second shelf-life at now is at or below zero. Evaluated on a fresh (never-expired) lot so
    // the boundary predicate itself is under test, not idempotence.
    [Fact]
    public void ExpiredIff_RemainingWholeSeconds_AtOrBelowZero()
    {
        Gen.Select(GenProducedAt, GenShelfLife, GenNow)
            .Sample((producedAt, shelfLife, now) =>
            {
                var lot = NewLot(producedAt, shelfLife);
                var shouldBeExpired = lot.RemainingWholeSeconds(now) <= 0;

                var transitioned = lot.TryExpireAt(now, out var evt);

                var expiredStateMatches = lot.IsExpired == shouldBeExpired;
                var transitionMatches = transitioned == shouldBeExpired;
                var eventMatches = shouldBeExpired ? evt is not null : evt is null;

                return expiredStateMatches && transitionMatches && eventMatches;
            }, iter: Iterations);
    }

    // Req 4.2 / 4.4: an already-expired lot remains unchanged and raises no further event on
    // re-invocation, regardless of the instant supplied on the second call (idempotence).
    [Fact]
    public void AlreadyExpiredLot_IsIdempotent_NoFurtherEvent()
    {
        // Generate a "now" strictly at/after expiry so the first call expires the lot, plus any
        // second instant (before or after) to prove the second call is a no-op either way.
        Gen.Select(GenProducedAt, GenShelfLife, GenNonNegativeDeltaSeconds, GenNow)
            .Sample((producedAt, shelfLife, pastExpirySeconds, secondNow) =>
            {
                var lot = NewLot(producedAt, shelfLife);

                // Force expiry: evaluate at expiry + a non-negative delta (remaining seconds <= 0).
                var atOrAfterExpiry = lot.ExpiresAt.AddSeconds(pastExpirySeconds);
                var firstTransition = lot.TryExpireAt(atOrAfterExpiry, out var firstEvent);

                if (!firstTransition || firstEvent is null || !lot.IsExpired)
                {
                    return false;
                }

                // Second invocation at an arbitrary instant: no transition, no event, still expired.
                var secondTransition = lot.TryExpireAt(secondNow, out var secondEvent);

                return !secondTransition && secondEvent is null && lot.IsExpired;
            }, iter: Iterations);
    }

    // Req 4.6: crossing the expiry boundary raises EXACTLY ONE LotExpired event regardless of how the
    // interval is subdivided. We split [start, end] (with end at/after expiry) into N intermediate
    // checkpoints and invoke TryExpireAt at each in order, counting events.
    [Fact]
    public void CrossingBoundary_RaisesExactlyOneEvent_RegardlessOfSubdivision()
    {
        var genSubdivisions = Gen.Int[1, 12];
        var genPastExpiry = Gen.Long[0, TenYearsSeconds];

        Gen.Select(GenProducedAt, GenShelfLife, genPastExpiry, genSubdivisions)
            .Sample((producedAt, shelfLife, pastExpirySeconds, subdivisions) =>
            {
                var lot = NewLot(producedAt, shelfLife);

                // Start well before expiry so the interval genuinely crosses the boundary.
                var start = lot.ExpiresAt.AddSeconds(-(shelfLife.TotalSeconds));
                var end = lot.ExpiresAt.AddSeconds(pastExpirySeconds);
                var totalSeconds = (end - start).TotalSeconds;

                var events = 0;
                for (var i = 1; i <= subdivisions; i++)
                {
                    var at = start.AddSeconds(totalSeconds * i / subdivisions);
                    if (lot.TryExpireAt(at, out var evt) && evt is not null)
                    {
                        events++;
                    }
                }

                // Final sweep exactly at/after expiry guarantees the boundary was crossed.
                if (lot.TryExpireAt(end, out var finalEvt) && finalEvt is not null)
                {
                    events++;
                }

                return events == 1 && lot.IsExpired;
            }, iter: Iterations);
    }

    // Req 4.7: determinism. Identical (ExpiresAt, now) yields identical RemainingShelfLife,
    // identical RemainingWholeSeconds, and identical expired outcome across two independent lots.
    [Fact]
    public void Determinism_IdenticalExpiryAndNow_YieldIdenticalResults()
    {
        Gen.Select(GenProducedAt, GenShelfLife, GenNow)
            .Sample((producedAt, shelfLife, now) =>
            {
                var lotA = NewLot(producedAt, shelfLife);
                var lotB = NewLot(producedAt, shelfLife);

                var sameExpiry = lotA.ExpiresAt == lotB.ExpiresAt;
                var sameSpan = lotA.RemainingShelfLife(now) == lotB.RemainingShelfLife(now);
                var sameSeconds = lotA.RemainingWholeSeconds(now) == lotB.RemainingWholeSeconds(now);

                var aExpired = lotA.TryExpireAt(now, out _);
                var bExpired = lotB.TryExpireAt(now, out _);
                var sameExpiredOutcome = aExpired == bExpired && lotA.IsExpired == lotB.IsExpired;

                return sameExpiry && sameSpan && sameSeconds && sameExpiredOutcome;
            }, iter: Iterations);
    }
}
