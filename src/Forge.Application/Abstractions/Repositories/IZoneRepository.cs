using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="TemperatureZone"/> aggregates (Req 1.4, 9.5). Interface only;
/// the EF Core implementation lives in Infrastructure (task 28.2). Used by slotting/put-away to resolve
/// candidate zones and by snapshot queries.
/// </summary>
public interface IZoneRepository
{
    /// <summary>Fetch a zone by its identity, or <c>null</c> if none exists.</summary>
    Task<TemperatureZone?> GetByIdAsync(ZoneId id, CancellationToken ct = default);

    /// <summary>Fetch every zone (used by slotting-strategy zone selection and snapshot queries).</summary>
    Task<IReadOnlyList<TemperatureZone>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Stage a new zone for insertion.</summary>
    void Add(TemperatureZone zone);

    /// <summary>Stage mutations to an existing zone (e.g. stored-quantity changes) for update.</summary>
    void Update(TemperatureZone zone);
}
