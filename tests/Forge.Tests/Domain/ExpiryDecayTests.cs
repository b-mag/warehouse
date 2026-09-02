using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;

namespace Forge.Tests.Domain;

/// <summary>
/// Unit tests for the deterministic Expiry_Decay rule edge cases (task 5.3):
/// zero/negative delta no-op, already-expired idempotence, and the exact-boundary
/// transition where remaining whole-second shelf-life is exactly zero.
/// Validates: Requirements 4.2, 4.4, 28.1.
/// </summary>
public sealed class ExpiryDecayTests
{
    private static readonly DateTimeOffset Produced = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TemperatureRange AnyRange = new(2m, 8m);
    private static readonly IReadOnlyList<string> AnyFlavors = new[] { "vanilla" };

    private static GelLot NewLot(TimeSpan shelfLife)
    {
        var formulation = new Formulation(AnyRange, shelfLife, AnyFlavors);
        return GelLot.Create(GelLotId.New(), GelTypeId.New(), formulation, Produced, quantity: 10);
    }

    // ---- Req 4.2: zero / negative "advance" leaves the lot unchanged (no expiry, no event) ----

    [Fact]
    public void EvaluatingBeforeExpiry_LeavesLotUnchanged_NoEvent()
    {
        // Shelf-life 1 hour; evaluate exactly at produced time (no advance) -> plenty remaining.
        var lot = NewLot(TimeSpan.FromHours(1));

        var transitioned = lot.TryExpireAt(Produced, out var evt);

        Assert.False(transitioned);
        Assert.Null(evt);
        Assert.False(lot.IsExpired);
        Assert.Equal(3600, lot.RemainingWholeSeconds(Produced));
    }

    [Theory]
    [InlineData(0)]      // zero delta
    [InlineData(-1)]     // negative delta (evaluate before produced time)
    [InlineData(-3600)]  // negative delta, one hour before
    public void ZeroOrNegativeDelta_IsNoOp_StateUnchanged(int deltaSeconds)
    {
        var lot = NewLot(TimeSpan.FromHours(1));
        var now = Produced.AddSeconds(deltaSeconds);
        var remainingBefore = lot.RemainingWholeSeconds(now);

        var transitioned = lot.TryExpireAt(now, out var evt);

        Assert.False(transitioned);
        Assert.Null(evt);
        Assert.False(lot.IsExpired);
        // Remaining shelf-life is a pure function of (ExpiresAt, now); nothing was mutated.
        Assert.Equal(remainingBefore, lot.RemainingWholeSeconds(now));
    }

    // ---- Req 4.4: already-expired idempotence (second call returns false, no event) ----

    [Fact]
    public void AlreadyExpired_SecondCall_ReturnsFalseAndNoEvent()
    {
        var lot = NewLot(TimeSpan.FromHours(1));
        var afterExpiry = lot.ExpiresAt.AddSeconds(1);

        // First call: transition to expired, exactly one event.
        var first = lot.TryExpireAt(afterExpiry, out var firstEvent);
        Assert.True(first);
        Assert.NotNull(firstEvent);
        Assert.Equal(lot.Id, firstEvent!.LotId);
        Assert.True(lot.IsExpired);

        // Second call: idempotent no-op, no further event, still expired.
        var second = lot.TryExpireAt(afterExpiry.AddHours(1), out var secondEvent);
        Assert.False(second);
        Assert.Null(secondEvent);
        Assert.True(lot.IsExpired);
    }

    [Fact]
    public void AlreadyExpired_ReEvaluatedBeforeExpiry_StaysExpired_NoEvent()
    {
        var lot = NewLot(TimeSpan.FromHours(1));

        Assert.True(lot.TryExpireAt(lot.ExpiresAt.AddSeconds(10), out _));
        Assert.True(lot.IsExpired);

        // Even evaluating at a time with positive remaining seconds does not un-expire the lot.
        var second = lot.TryExpireAt(Produced, out var evt);

        Assert.False(second);
        Assert.Null(evt);
        Assert.True(lot.IsExpired);
    }

    // ---- Req 4.3/4.4 boundary: remaining whole seconds == 0 => expired with exactly one event ----

    [Fact]
    public void ExactBoundary_RemainingWholeSecondsZero_TransitionsWithOneEvent()
    {
        var lot = NewLot(TimeSpan.FromHours(1));

        // Evaluate exactly at expiry: remaining whole seconds is exactly zero.
        Assert.Equal(0, lot.RemainingWholeSeconds(lot.ExpiresAt));

        var transitioned = lot.TryExpireAt(lot.ExpiresAt, out var evt);

        Assert.True(transitioned);
        Assert.NotNull(evt);
        Assert.Equal(lot.Id, evt!.LotId);
        Assert.Equal(lot.ExpiresAt, evt.At);
        Assert.True(lot.IsExpired);
    }

    [Fact]
    public void JustBeforeBoundary_SubSecondRemaining_TruncatesToZero_Expires()
    {
        var lot = NewLot(TimeSpan.FromHours(1));

        // 500ms before expiry: raw span is positive but whole-second count truncates to zero,
        // so the whole-second rule treats it as expired (Req 4.1 truncation + Req 4.3 boundary).
        var justBefore = lot.ExpiresAt.AddMilliseconds(-500);
        Assert.True(lot.RemainingShelfLife(justBefore) > TimeSpan.Zero);
        Assert.Equal(0, lot.RemainingWholeSeconds(justBefore));

        var transitioned = lot.TryExpireAt(justBefore, out var evt);

        Assert.True(transitioned);
        Assert.NotNull(evt);
        Assert.True(lot.IsExpired);
    }

    [Fact]
    public void OneSecondBeforeExpiry_NotExpired_NoEvent()
    {
        var lot = NewLot(TimeSpan.FromHours(1));
        var oneSecondBefore = lot.ExpiresAt.AddSeconds(-1);

        Assert.Equal(1, lot.RemainingWholeSeconds(oneSecondBefore));

        var transitioned = lot.TryExpireAt(oneSecondBefore, out var evt);

        Assert.False(transitioned);
        Assert.Null(evt);
        Assert.False(lot.IsExpired);
    }
}
