namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a warehouse task (Req 2.3, 23.4).
/// </summary>
public sealed record WarehouseTaskDto(
    Guid Id,
    string Type,
    string Status,
    Guid? Worker);
