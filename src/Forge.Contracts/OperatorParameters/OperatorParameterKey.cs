namespace Forge.Contracts.OperatorParameters;

/// <summary>
/// The canonical keys identifying each of the six operator-adjustable parameters (Req 20.1).
/// Clients submit a single change keyed by one of these values; the Application layer
/// validates the accompanying value against the matching range in
/// <see cref="OperatorParameterRanges"/>.
/// </summary>
public static class OperatorParameterKey
{
    public const string SimSpeed = "sim-speed";
    public const string WorkersOnShift = "workers-on-shift";
    public const string OpenDockBays = "open-dock-bays";
    public const string InboundRate = "inbound-rate";
    public const string DemandMultiplier = "demand-multiplier";
    public const string SlottingStrategy = "slotting-strategy";
}
