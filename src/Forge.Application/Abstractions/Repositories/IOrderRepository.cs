using Forge.Domain.Colonies;
using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="ColonyOrder"/> aggregates (Req 1.4, 9.5). Interface only; the
/// EF Core implementation lives in Infrastructure (task 28.2). Used by the create-order handler
/// (task 24.1) to persist new orders and by snapshot queries.
/// </summary>
public interface IOrderRepository
{
    /// <summary>Fetch an order by its identity, or <c>null</c> if none exists.</summary>
    Task<ColonyOrder?> GetByIdAsync(ColonyOrderId id, CancellationToken ct = default);

    /// <summary>Fetch every order (used by fulfillment scheduling and snapshot queries).</summary>
    Task<IReadOnlyList<ColonyOrder>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Fetch all orders placed by a given colony.</summary>
    Task<IReadOnlyList<ColonyOrder>> GetByColonyAsync(ColonyId colonyId, CancellationToken ct = default);

    /// <summary>Stage a newly created order for insertion (task 24.1).</summary>
    void Add(ColonyOrder order);

    /// <summary>Stage mutations to an existing order for update.</summary>
    void Update(ColonyOrder order);
}
