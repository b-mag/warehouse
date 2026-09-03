namespace Forge.Simulation;

/// <summary>
/// Tunable configuration for the Phase-1 Simulation input driver: the seeds that make generation
/// reproducible, the initial operator parameters (inbound arrival rate — Req 20.5; colony demand
/// multiplier — Req 20.6), and the wall-clock cadence of the tick loop.
/// <para>
/// The two operator parameters exposed here (<see cref="InitialArrivalRatePerHour"/> and
/// <see cref="DemandMultiplier"/>) are the <b>seam</b> for operator-parameter wiring. Until the
/// operator-parameter subsystem is wired into Simulation, they are injected configurable values with
/// sane defaults; the arrival rate is pushed onto the <see cref="Arrivals.ArrivalGenerator"/> each
/// tick and the multiplier is passed into demand generation, so a later change source (the operator
/// parameter store) can update these without touching the loop.
/// </para>
/// </summary>
public sealed class SimulationDriverOptions
{
    /// <summary>Seed for the inbound-arrival generator's PRNG stream (Req 11.1 reproducibility).</summary>
    public ulong ArrivalSeed { get; set; } = 0x5EED_A55_1_A22_1UL;

    /// <summary>Seed for the colony-demand generator's PRNG streams (Req 12.7 reproducibility).</summary>
    public int DemandSeed { get; set; } = 0x5EED_D3;

    /// <summary>Seed for the temperature-reading generator's PRNG streams (Req 6.2 reproducibility).</summary>
    public int TemperatureSeed { get; set; } = 0x5EED_7E;

    /// <summary>
    /// Initial inbound arrival rate in arrivals per simulated hour (operator parameter, Req 20.5). Must
    /// be finite and non-negative; a zero rate produces no arrivals. Pushed onto the arrival generator
    /// each tick so an operator change to the rate takes effect for subsequent simulated time.
    /// </summary>
    public double InitialArrivalRatePerHour { get; set; } = 8.0;

    /// <summary>
    /// The colony demand multiplier applied to generated orders (operator parameter, Req 20.6). Must be
    /// finite and non-negative. Passed into demand generation each tick.
    /// </summary>
    public double DemandMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Wall-clock interval between tick-loop iterations. A modest interval (default ~100ms) keeps
    /// accelerated time advancing smoothly while bounding CPU. Made configurable so tests can drive the
    /// loop deterministically without waiting on real wall time. Must be positive.
    /// </summary>
    public TimeSpan LoopInterval { get; set; } = TimeSpan.FromMilliseconds(100);
}
