using Forge.Application.Abstractions;
using Forge.Simulation.Clock;

namespace Forge.Tests.Simulation;

/// <summary>
/// Unit tests for the accelerated <see cref="SimulationClock"/> (task 27.1). Covers the mode-driven
/// conversion of a wall delta to a simulated delta — real-time advances at the wall rate, accelerated
/// scales by the factor, and paused applies zero and does not move <see cref="IClock.Now"/> — along
/// with Configure/Pause/Resume transitions and deterministic Now progression.
///
/// Validates: Requirements 10.2, 10.3, 10.5, 10.6, 10.7.
/// </summary>
public sealed class SimulationClockTests
{
    private static readonly DateTimeOffset Epoch = new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Req 10.3: real-time advance equals the wall delta ----

    [Fact]
    public void RealTime_Advance_AppliesWallDelta_AndMovesNow()
    {
        var clock = new SimulationClock(Epoch, ClockMode.RealTime);
        var wallDelta = TimeSpan.FromSeconds(30);

        var applied = clock.Advance(wallDelta);

        Assert.Equal(wallDelta, applied);
        Assert.Equal(Epoch + wallDelta, clock.Now);
        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);
    }

    [Fact]
    public void RealTime_IsTheDefaultMode()
    {
        var clock = new SimulationClock(Epoch);

        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);
        Assert.Equal(Epoch, clock.Now);
    }

    // ---- Req 10.2: accelerated advance scales by the factor ----

    [Theory]
    [InlineData(2.0)]
    [InlineData(10.0)]
    [InlineData(60.0)]
    public void Accelerated_Advance_ScalesWallDeltaByFactor(double factor)
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, factor);
        var wallDelta = TimeSpan.FromSeconds(5);

        var applied = clock.Advance(wallDelta);

        Assert.Equal(wallDelta * factor, applied);
        Assert.Equal(Epoch + wallDelta * factor, clock.Now);
    }

    [Fact]
    public void Accelerated_AdvancesFasterThanWall()
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 4.0);

        var applied = clock.Advance(TimeSpan.FromSeconds(1));

        // A single wall second yields more than one simulated second when accelerated (Req 10.2).
        Assert.True(applied > TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(4), applied);
    }

    [Fact]
    public void Accelerated_RequiresFactorAtLeastOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 0.5));
    }

    // ---- Req 10.5: paused advance returns zero and does not move Now ----

    [Fact]
    public void Paused_Advance_ReturnsZero_AndDoesNotMoveNow()
    {
        var clock = new SimulationClock(Epoch, ClockMode.RealTime);
        clock.Pause();

        var applied = clock.Advance(TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.Zero, applied);
        Assert.Equal(Epoch, clock.Now);
        Assert.Equal(ClockMode.Paused, clock.Mode);
    }

    [Fact]
    public void Paused_MultipleAdvances_LeaveNowUnchanged()
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 8.0);
        clock.Pause();

        clock.Advance(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(Epoch, clock.Now);
    }

    // ---- Non-positive host deltas apply nothing regardless of mode ----

    [Fact]
    public void NonPositiveWallDelta_AppliesZero_AndDoesNotMoveNow()
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 10.0);

        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.FromSeconds(-5)));
        Assert.Equal(Epoch, clock.Now);
    }

    // ---- Req 10.2/10.3/10.5: Configure / Pause / Resume transitions ----

    [Fact]
    public void Configure_SwitchesModeAndFactor_AffectingSubsequentAdvance()
    {
        var clock = new SimulationClock(Epoch, ClockMode.RealTime);

        clock.Configure(ClockMode.Accelerated, 3.0);
        Assert.Equal(ClockMode.Accelerated, clock.Mode);
        Assert.Equal(3.0, clock.AccelerationFactor);

        var applied = clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(6), applied);

        clock.Configure(ClockMode.RealTime, 1.0);
        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);

        var applied2 = clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), applied2);
    }

    [Fact]
    public void Resume_RestoresTheModeAndFactorThatWereActiveBeforePause()
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 12.0);

        clock.Pause();
        Assert.Equal(ClockMode.Paused, clock.Mode);

        clock.Resume();
        Assert.Equal(ClockMode.Accelerated, clock.Mode);
        Assert.Equal(12.0, clock.AccelerationFactor);

        // After resume, advancement scales by the restored factor again.
        var applied = clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(12), applied);
    }

    [Fact]
    public void PauseThenResume_ContinuesAdvancingFromWhereNowStopped()
    {
        var clock = new SimulationClock(Epoch, ClockMode.RealTime);

        var before = clock.Advance(TimeSpan.FromSeconds(10));
        var atPause = clock.Now;
        Assert.Equal(TimeSpan.FromSeconds(10), before);

        clock.Pause();
        clock.Advance(TimeSpan.FromSeconds(100)); // ignored while paused
        Assert.Equal(atPause, clock.Now);

        clock.Resume();
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(atPause + TimeSpan.FromSeconds(5), clock.Now);
    }

    [Fact]
    public void Resume_WithoutPause_IsANoOp()
    {
        var clock = new SimulationClock(Epoch, ClockMode.RealTime);

        clock.Resume();

        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);
    }

    [Fact]
    public void Pause_WhenAlreadyPaused_PreservesTheOriginalResumeState()
    {
        var clock = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 6.0);

        clock.Pause();
        clock.Pause(); // second pause must not overwrite the remembered running state
        clock.Resume();

        Assert.Equal(ClockMode.Accelerated, clock.Mode);
        Assert.Equal(6.0, clock.AccelerationFactor);
    }

    // ---- Determinism: identical construction + identical Advance sequence => identical Now ----

    [Fact]
    public void IdenticalConstructionAndAdvanceSequence_YieldIdenticalNowProgression()
    {
        var deltas = new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMinutes(2),
        };

        var a = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 7.5);
        var b = new SimulationClock(Epoch, ClockMode.Accelerated, accelerationFactor: 7.5);

        foreach (var delta in deltas)
        {
            var appliedA = a.Advance(delta);
            var appliedB = b.Advance(delta);

            Assert.Equal(appliedA, appliedB);
            Assert.Equal(a.Now, b.Now);
        }

        // Now progression does not depend on real wall-clock time.
        Assert.Equal(a.Now, b.Now);
    }
}
