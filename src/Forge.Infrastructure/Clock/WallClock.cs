using Forge.Application.Abstractions;

namespace Forge.Infrastructure.Clock;

/// <summary>
/// The real-world, non-accelerated <see cref="IClock"/> implementation and the WMS Core's default
/// clock for a non-simulated deployment (design "The accelerated <c>SimulationClock</c> lives in
/// <c>Forge.Simulation</c>; the wall-clock <c>WallClock</c> lives in <c>Forge.Infrastructure</c> and
/// is the real-world default"; Req 10.6, 10.7, 2.6).
///
/// <para>A wall clock is <b>not</b> accelerated: its <see cref="Mode"/> is
/// <see cref="ClockMode.RealTime"/> and its <see cref="AccelerationFactor"/> is always
/// <c>1.0</c>. <see cref="Now"/> tracks real wall-clock time, and <see cref="Advance"/> applies the
/// host-loop wall delta unscaled (factor 1), so simulated time equals wall time.</para>
///
/// <para><b>No simulation loop or accelerated clock resides in Infrastructure.</b> This type only
/// implements the <see cref="IClock"/> abstraction the core depends on (Req 10.7, 2.6); the tick
/// loop and the accelerated clock live in the pluggable Simulation input driver.</para>
///
/// <para><b>Pause semantics (Req 10.5).</b> While paused, wall time stops being observed:
/// <see cref="Now"/> freezes at the instant of the pause and <see cref="Advance"/> returns
/// <see cref="TimeSpan.Zero"/>. <see cref="Resume"/> continues from real time without replaying the
/// wall time that elapsed while paused. Because a wall clock cannot be accelerated,
/// <see cref="Configure"/> only toggles between running (real-time) and paused; the acceleration
/// factor is always coerced to <c>1.0</c>.</para>
///
/// <para><b>Thread safety.</b> All mutating operations and time reads are guarded by a lock so the
/// clock can be advanced by a host loop while being observed by other threads.</para>
/// </summary>
public sealed class WallClock : IClock
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    private bool _paused;

    /// <summary>The wall time captured at the moment the clock was paused; frozen while paused.</summary>
    private DateTimeOffset _pausedAt;

    /// <summary>
    /// Creates a wall clock backed by the system clock (<see cref="TimeProvider.System"/>).
    /// </summary>
    public WallClock() : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Creates a wall clock backed by an explicit <paramref name="timeProvider"/>. This overload lets
    /// tests drive real time deterministically while exercising the identical wall-rate logic.
    /// </summary>
    /// <param name="timeProvider">The source of real time. Must not be <see langword="null"/>.</param>
    public WallClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns current real (UTC) wall time while running; returns the frozen instant captured at the
    /// last <see cref="Pause"/> while paused.
    /// </remarks>
    public DateTimeOffset Now
    {
        get
        {
            lock (_gate)
            {
                return _paused ? _pausedAt : _timeProvider.GetUtcNow();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>A wall clock is <see cref="ClockMode.RealTime"/> while running and reports
    /// <see cref="ClockMode.Paused"/> while paused; it is never <see cref="ClockMode.Accelerated"/>.</remarks>
    public ClockMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _paused ? ClockMode.Paused : ClockMode.RealTime;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Always <c>1.0</c>: a wall clock advances at the wall-clock rate and is never accelerated.</remarks>
    public double AccelerationFactor => 1.0;

    /// <inheritdoc />
    /// <remarks>
    /// A wall clock only supports running (real-time) or paused; <paramref name="accelerationFactor"/>
    /// is ignored and the factor stays <c>1.0</c>. Requesting <see cref="ClockMode.Paused"/> pauses the
    /// clock; any other mode resumes it at wall rate.
    /// </remarks>
    public void Configure(ClockMode mode, double accelerationFactor)
    {
        if (mode == ClockMode.Paused)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            if (_paused)
            {
                return;
            }

            _pausedAt = _timeProvider.GetUtcNow();
            _paused = true;
        }
    }

    /// <inheritdoc />
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// At the wall rate (factor 1) the applied delta equals <paramref name="wallDelta"/> when running;
    /// negative deltas are clamped to <see cref="TimeSpan.Zero"/>, and <see cref="TimeSpan.Zero"/> is
    /// returned while paused (Req 10.5).
    /// </remarks>
    public TimeSpan Advance(TimeSpan wallDelta)
    {
        lock (_gate)
        {
            if (_paused || wallDelta <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            // Wall rate: acceleration factor 1, so the applied delta equals the wall delta.
            return wallDelta;
        }
    }
}
