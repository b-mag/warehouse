namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable full-state snapshot pushed to clients on connect and on change (Req 2.3, 23.3, 23.4).
/// </summary>
public sealed record SimulationSnapshotDto(
    IReadOnlyList<TemperatureZoneDto> Zones,
    IReadOnlyList<GelLotDto> Lots,
    IReadOnlyList<AgentDto> Agents,
    IReadOnlyList<StarshipDto> Starships,
    BacklogMetricsDto Metrics,
    OperatorParameterStateDto Parameters,
    DateTimeOffset SimulatedAt,
    TrainDto? Train,
    /// <summary>Lots still on the inbound rail / receiving queue (not yet in a zone cube).</summary>
    IReadOnlyList<Guid> InboundQueueLotIds,
    /// <summary>Lots carried by agents between rail and storage (or pick→ship).</summary>
    IReadOnlyList<Guid> InTransitLotIds);
