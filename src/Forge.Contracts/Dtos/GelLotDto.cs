namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a gel lot for rendering/reporting (Req 2.3, 23.4).
/// </summary>
public sealed record GelLotDto(
    Guid Id,
    Guid GelTypeId,
    DateTimeOffset ExpiresAt,
    int Quantity,
    bool IsExpired,
    bool AtRisk,
    Guid? ZoneId);
