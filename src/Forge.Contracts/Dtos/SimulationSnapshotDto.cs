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
    OperatorParameterStateDto Parameters);
