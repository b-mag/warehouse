namespace Forge.Contracts.Dtos;

/// <summary>
/// Inventory projection pushed less frequently than positions so holding-area cubes and
/// zone stored counts stay in sync with PutAway/Pick without re-sending the full snapshot.
/// </summary>
public sealed record InventoryUpdateDto(
    IReadOnlyList<TemperatureZoneDto> Zones,
    IReadOnlyList<GelLotDto> Lots);
