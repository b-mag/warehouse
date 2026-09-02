namespace Forge.Contracts.OperatorParameters;

/// <summary>
/// Validation ranges for the six operator-adjustable parameters (Req 20.1, 20.3–20.8).
/// These constants/values are the single source of truth the Application layer and
/// clients reference when validating a change request. Bounds that depend on
/// deployment configuration (worker maximum, physically modeled dock bays) expose a
/// lower bound here; the configured upper bound is supplied by the Application layer
/// at validation time.
/// </summary>
public static class OperatorParameterRanges
{
    /// <summary>
    /// Simulation speed. 0 = paused, 1 = real-time, &gt;1 = accelerated (Req 20.2).
    /// Must be non-negative.
    /// </summary>
    public const double SimSpeedMin = 0.0;

    /// <summary>Lower bound for workers on shift (Req 20.3). Upper bound is a configured maximum.</summary>
    public const int WorkersOnShiftMin = 0;

    /// <summary>Lower bound for open dock bays (Req 20.4). Upper bound is the count of physically modeled bays.</summary>
    public const int OpenDockBaysMin = 0;

    /// <summary>Inbound arrival rate must be non-negative (Req 20.5).</summary>
    public const double InboundRateMin = 0.0;

    /// <summary>Colony demand multiplier must be non-negative (Req 20.6).</summary>
    public const double DemandMultiplierMin = 0.0;

    /// <summary>Returns true when a sim-speed value is within range (Req 20.2, 20.8).</summary>
    public static bool IsValidSimSpeed(double value) =>
        value >= SimSpeedMin && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Returns true when a workers-on-shift value is within [0, configuredMax] (Req 20.3, 20.8).</summary>
    public static bool IsValidWorkersOnShift(int value, int configuredMax) =>
        value >= WorkersOnShiftMin && value <= configuredMax;

    /// <summary>Returns true when an open-dock-bays value is within [0, modeledBays] (Req 20.4, 20.8).</summary>
    public static bool IsValidOpenDockBays(int value, int modeledBays) =>
        value >= OpenDockBaysMin && value <= modeledBays;

    /// <summary>Returns true when an inbound-rate value is non-negative and finite (Req 20.5, 20.8).</summary>
    public static bool IsValidInboundRate(double value) =>
        value >= InboundRateMin && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Returns true when a demand-multiplier value is non-negative and finite (Req 20.6, 20.8).</summary>
    public static bool IsValidDemandMultiplier(double value) =>
        value >= DemandMultiplierMin && !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>Returns true when a slotting-strategy key is recognized (Req 20.7, 20.8).</summary>
    public static bool IsValidSlottingStrategy(string? key) =>
        SlottingStrategyKey.IsValid(key);
}
