namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a temperature zone (Req 2.3, 23.4).
/// </summary>
public sealed record TemperatureZoneDto(
    Guid Id,
    decimal MinC,
    decimal MaxC,
    int Capacity,
    int Stored);
