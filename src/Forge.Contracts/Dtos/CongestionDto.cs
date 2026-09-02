namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of reservation congestion (Req 2.3, 23.4).
/// </summary>
public sealed record CongestionDto(
    int ReservedSegments,
    int QueuedAgents,
    IReadOnlyList<CellDto> HotCells);
