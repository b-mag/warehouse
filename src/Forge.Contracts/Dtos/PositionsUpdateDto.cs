namespace Forge.Contracts.Dtos;

/// <summary>
/// Lightweight real-time positions payload (Req 23.4).
/// Designed for frequent client updates without re-querying inventory/zone/lot repositories.
/// </summary>
public sealed record PositionsUpdateDto(
    IReadOnlyList<AgentDto> Agents,
    IReadOnlyList<StarshipDto> Starships,
    /// <summary>Lot ids currently on the inbound train/conveyor.</summary>
    IReadOnlyList<Guid> InboundQueueLotIds,
    /// <summary>Lot ids currently being carried/in-transit.</summary>
    IReadOnlyList<Guid> InTransitLotIds,
    TrainDto? Train,
    DateTimeOffset SimulatedAt);
