using Forge.Domain.Colonies;
using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="Colony"/> aggregates (Req 1.4, 9.5). Interface only; the
/// EF Core implementation lives in Infrastructure (task 28.2). Used by the create-order handler
/// (task 24.1) to resolve the ordering colony and by snapshot queries.
/// </summary>
public interface IColonyRepository
{
    /// <summary>Fetch a colony by its identity, or <c>null</c> if none exists.</summary>
    Task<Colony?> GetByIdAsync(ColonyId id, CancellationToken ct = default);

    /// <summary>Fetch every colony (used by demand/snapshot queries).</summary>
    Task<IReadOnlyList<Colony>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Stage a new colony for insertion.</summary>
    void Add(Colony colony);

    /// <summary>Stage mutations to an existing colony for update.</summary>
    void Update(Colony colony);
}
