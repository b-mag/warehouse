namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of the full operator-parameter state (Req 2.3, 23.4, 20).
/// </summary>
public sealed record OperatorParameterStateDto(
    double SimSpeed,
    int WorkersOnShift,
    int OpenDockBays,
    double InboundRate,
    double DemandMultiplier,
    string SlottingStrategy);
