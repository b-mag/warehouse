namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a starship loading window (Req 2.3, 23.4).
/// </summary>
public sealed record LoadingWindowDto(DateTimeOffset Start, DateTimeOffset End);
