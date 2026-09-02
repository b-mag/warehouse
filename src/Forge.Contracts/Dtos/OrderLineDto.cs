namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a single colony order line (Req 2.3, 23.4).
/// </summary>
public sealed record OrderLineDto(Guid GelTypeId, int Quantity);
