namespace Forge.Contracts.Dtos;

/// <summary>
/// A single grid cell coordinate. Immutable DTO shared with the Game (Req 2.3, 23.4).
/// </summary>
public sealed record CellDto(int X, int Y);
