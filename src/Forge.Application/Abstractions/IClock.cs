namespace Forge.Application.Abstractions;

/// <summary>
/// The mode a general <see cref="IClock"/> runs in (Req 10.1, 10.2, 10.5).
/// </summary>
public enum ClockMode
{
    /// <summary>Advances at the wall-clock rate (acceleration factor 1). A real-world default.</summary>
    RealTime,

    /// <summary>Advances faster than wall time by an acceleration factor &gt; 1 (accelerated simulation).</summary>
    Accelerated,

    /// <summary>Advancement is suspended; <see cref="IClock.Advance"/> returns zero delta (Req 10.5).</summary>
    Paused,
}

/// <summary>
/// A general clock abstraction (renamed from <c>ISimulationClock</c>) the WMS Core depends on for
/// the notion of "now" and for advancing simulated/real time (design "The Input Driver seam"; Req 10).
/// It is NOT simulation-specific: it is satisfied by an accelerated <c>SimulationClock</c> (in
/// <c>Forge.Simulation</c>, task 27.1) OR a <c>WallClock</c> (in <c>Forge.Infrastructure</c>, task 31.1)
/// for a real-world deployment. The core references only this abstraction.
/// </summary>
public interface IClock
{
    /// <summary>The current time (simulated or wall), used everywhere the core needs "now".</summary>
    DateTimeOffset Now { get; }

    /// <summary>The clock's current mode: real-time, accelerated, or paused.</summary>
    ClockMode Mode { get; }

    /// <summary>The acceleration factor: &gt;= 1 when accelerated; 1 for a wall clock.</summary>
    double AccelerationFactor { get; }

    /// <summary>Reconfigure the clock's mode and acceleration factor (e.g. from an operator parameter).</summary>
    void Configure(ClockMode mode, double accelerationFactor);

    /// <summary>Pause advancement; subsequent <see cref="Advance"/> calls apply zero delta (Req 10.5).</summary>
    void Pause();

    /// <summary>Resume advancement after a <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>
    /// Advance the clock in response to a host-loop wall-time delta, returning the delta actually
    /// applied. Returns <see cref="TimeSpan.Zero"/> while paused (Req 10.5); scales
    /// <paramref name="wallDelta"/> by the mode/acceleration factor otherwise.
    /// </summary>
    TimeSpan Advance(TimeSpan wallDelta);
}
