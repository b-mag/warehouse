namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a moving agent (worker/forklift) and its current path (Req 2.3, 23.4).
/// </summary>
public sealed record AgentDto(
    Guid Id,
    int X,
    int Y,
    IReadOnlyList<CellDto> PathCells,
    double CellsPerSecond);
