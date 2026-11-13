namespace Forge.Contracts.Dtos;

/// <summary>
/// Authoritative inbound train consist for the rail choreography (arrive → unload → depart).
/// </summary>
public sealed record TrainDto(
    Guid Id,
    string Phase,
    int CarCount,
    IReadOnlyList<Guid> LotIds);
