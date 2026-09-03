using Forge.Application.Abstractions;

namespace Forge.Simulation.Clock;

/// <summary>
/// The Phase-1 accelerated <see cref="IClock"/> implementation supplied by the Simulation_Driver
/// (design "The Input Driver seam"; Req 10.1, 10.2, 10.3, 10.5). It converts a wall-clock delta from
/// the host loop into a simulated delta based on the current <see cref="ClockMode"/> and acceleration
/// factor, and advances <see cref="Now"/> by exactly the applied delta.
///
/// <para>
/// This is the accelerated clock, which lives in <c>Forge.Simulation</c> (moved OUT of Infrastructure).
/// The WMS Core depends only on the <see cref="IClock"/> abstraction (Req 10.6, 10.7); a real-world
/// deployment would instead supply a wall-clock <see cref="IClock"/>.
/// </para>
///
/// <para>
/// Advancement is deterministic: an instance constructed from the same epoch, mode, and factor and then
/// driven by an identical sequence of <see cref="Advance(TimeSpan)"/> calls yields an identical
/// <see cref="Now"/> progression, with no dependence on real wall-clock time.
/// </para>
/// </summary>
public sealed class SimulationClock : IClock
{
    /// <summary>The default acceleration factor for real-time advancement (wall rate).</summary>
    private const double RealTimeFactor = 1.0;

    /// <summary>The mode/factor to restore on <see cref="Resume"/> after a <see cref="Pause"/>.</summary>
    private ClockMode _resumeMode;
    private double _resumeFactor;

    /// <summary>
    /// Create an accelerated clock at the given start time and mode.
    /// </summary>
    /// <param name="start">The initial value of <see cref="Now"/>.</param>
    /// <param name="mode">The starting mode (defaults to real-time).</param>
    /// <param name="accelerationFactor">
    /// The starting acceleration factor. Ignored (treated as 1) unless <paramref name="mode"/> is
    /// <see cref="ClockMode.Accelerated"/>; must be &gt;= 1 when accelerated.
    /// </param>
    public SimulationClock(
        DateTimeOffset start,
        ClockMode mode = ClockMode.RealTime,
        double accelerationFactor = RealTimeFactor)
    {
        Now = start;
        (Mode, AccelerationFactor) = Normalize(mode, accelerationFactor);
        _resumeMode = Mode == ClockMode.Paused ? ClockMode.RealTime : Mode;
        _resumeFactor = Mode == ClockMode.Paused ? RealTimeFactor : AccelerationFactor;
    }

    /// <inheritdoc />
    public DateTimeOffset Now { get; private set; }

    /// <inheritdoc />
    public ClockMode Mode { get; private set; }

    /// <inheritdoc />
    public double AccelerationFactor { get; private set; }

    /// <inheritdoc />
    public void Configure(ClockMode mode, double accelerationFactor)
    {
        (Mode, AccelerationFactor) = Normalize(mode, accelerationFactor);

        // Remember the configured (non-paused) mode/factor so a later Resume restores it.
        if (Mode != ClockMode.Paused)
        {
            _resumeMode = Mode;
            _resumeFactor = AccelerationFactor;
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (Mode == ClockMode.Paused)
        {
            return;
        }

        // Preserve the current mode/factor so Resume can restore the prior running state.
        _resumeMode = Mode;
        _resumeFactor = AccelerationFactor;
        Mode = ClockMode.Paused;
    }

    /// <inheritdoc />
    public void Resume()
    {
        if (Mode != ClockMode.Paused)
        {
            return;
        }

        Mode = _resumeMode;
        AccelerationFactor = _resumeFactor;
    }

    /// <inheritdoc />
    public TimeSpan Advance(TimeSpan wallDelta)
    {
        // Paused (Req 10.5) or a non-positive host delta applies no simulated time and does not move Now.
        if (Mode == ClockMode.Paused || wallDelta <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        // Real-time advances at wall rate (Req 10.3); accelerated scales by the factor (Req 10.2).
        var applied = Mode == ClockMode.Accelerated
            ? wallDelta * AccelerationFactor
            : wallDelta;

        Now += applied;
        return applied;
    }

    /// <summary>
    /// Validate and normalize a (mode, factor) pair: real-time and paused pin the factor to 1;
    /// accelerated requires a factor &gt;= 1.
    /// </summary>
    private static (ClockMode Mode, double Factor) Normalize(ClockMode mode, double accelerationFactor)
    {
        switch (mode)
        {
            case ClockMode.Accelerated:
                if (double.IsNaN(accelerationFactor) || accelerationFactor < 1.0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(accelerationFactor),
                        accelerationFactor,
                        "Accelerated mode requires an acceleration factor greater than or equal to 1.");
                }

                return (ClockMode.Accelerated, accelerationFactor);

            case ClockMode.RealTime:
            case ClockMode.Paused:
                // Real-time and paused clocks run at the wall rate (factor 1).
                return (mode, RealTimeFactor);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown clock mode.");
        }
    }
}
