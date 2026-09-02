namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a colony order with its delivery window (Req 2.3, 23.4).
/// </summary>
public sealed record ColonyOrderDto(
    Guid Id,
    Guid Colony,
    IReadOnlyList<OrderLineDto> Lines,
    DateTimeOffset From,
    DateTimeOffset To);
