namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of a demand forecast. <see cref="State"/> is one of
/// Accepted | Overridden | Accepted_By_Default | Pending (Req 2.3, 23.4).
/// </summary>
public sealed record DemandForecastDto(
    Guid Colony,
    Guid GelType,
    TimeSpan Horizon,
    long Quantity,
    bool IsFallback,
    string State);
