using Forge.Application.Abstractions;
using Forge.Infrastructure.Clock;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit tests for the real-world default <see cref="WallClock"/> (task 31.1): a non-accelerated
/// <see cref="IClock"/> whose <see cref="IClock.Now"/> tracks wall time, whose
/// <see cref="IClock.Mode"/> is <see cref="ClockMode.RealTime"/> with an
/// <see cref="IClock.AccelerationFactor"/> of 1, and whose <see cref="IClock.Advance"/> applies the
/// host-loop wall delta unscaled (returning zero while paused, Req 10.5).
/// Validates: Requirements 10.6, 10.7, 2.6.
/// </summary>
public sealed class WallClockTests
{
    /// <summary>A controllable <see cref="TimeProvider"/> so tests can drive "real" time deterministically.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static readonly DateTimeOffset Start =
        new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mode_is_real_time_when_running()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));

        Assert.Equal(ClockMode.RealTime, clock.Mode);
    }

    [Fact]
    public void AccelerationFactor_is_one()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));

        Assert.Equal(1.0, clock.AccelerationFactor);
    }

    [Fact]
    public void Now_advances_forward_as_real_time_elapses()
    {
        var time = new ManualTimeProvider(Start);
        var clock = new WallClock(time);

        var before = clock.Now;
        time.Advance(TimeSpan.FromSeconds(5));
        var after = clock.Now;

        Assert.Equal(Start, before);
        Assert.True(after > before);
        Assert.Equal(Start + TimeSpan.FromSeconds(5), after);
    }

    [Fact]
    public void Advance_at_wall_rate_returns_the_wall_delta()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));

        var wallDelta = TimeSpan.FromSeconds(3);
        var applied = clock.Advance(wallDelta);

        Assert.Equal(wallDelta, applied);
    }

    [Fact]
    public void Advance_with_zero_or_negative_delta_returns_zero()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));

        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Pause_freezes_now_and_advance_returns_zero()
    {
        var time = new ManualTimeProvider(Start);
        var clock = new WallClock(time);

        clock.Pause();
        var frozen = clock.Now;
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(ClockMode.Paused, clock.Mode);
        Assert.Equal(frozen, clock.Now);
        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void Resume_returns_to_real_time_rate()
    {
        var time = new ManualTimeProvider(Start);
        var clock = new WallClock(time);

        clock.Pause();
        time.Advance(TimeSpan.FromSeconds(10));
        clock.Resume();

        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(Start + TimeSpan.FromSeconds(10), clock.Now);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.Advance(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Configure_paused_pauses_and_keeps_factor_one()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));

        clock.Configure(ClockMode.Paused, 8.0);

        Assert.Equal(ClockMode.Paused, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);
        Assert.Equal(TimeSpan.Zero, clock.Advance(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Configure_non_paused_resumes_at_wall_rate_and_ignores_factor()
    {
        var clock = new WallClock(new ManualTimeProvider(Start));
        clock.Pause();

        clock.Configure(ClockMode.Accelerated, 10.0);

        Assert.Equal(ClockMode.RealTime, clock.Mode);
        Assert.Equal(1.0, clock.AccelerationFactor);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Advance(TimeSpan.FromSeconds(1)));
    }
}
