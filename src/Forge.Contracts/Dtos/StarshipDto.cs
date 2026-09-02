namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a starship and its loading windows (Req 2.3, 23.4).
/// </summary>
public sealed record StarshipDto(
    Guid Id,
    int Capacity,
    int Loaded,
    Guid DestinationColony,
    IReadOnlyList<LoadingWindowDto> Windows);
