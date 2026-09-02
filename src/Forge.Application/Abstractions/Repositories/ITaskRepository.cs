using Forge.Domain.Common;
using Forge.Domain.Tasks;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="WarehouseTask"/> aggregates (Req 1.4, 9.5). Interface only; the
/// EF Core implementation lives in Infrastructure (task 28.2). Supports the fulfillment/put-away handlers
/// (tasks 24.1, 24.2), the shift-gated assignment flow (task 19.1), and the complete-task handler (task 24.5).
/// </summary>
public interface ITaskRepository
{
    /// <summary>Fetch a task by its identity, or <c>null</c> if none exists.</summary>
    Task<WarehouseTask?> GetByIdAsync(WarehouseTaskId id, CancellationToken ct = default);

    /// <summary>Fetch every task (used by snapshot queries).</summary>
    Task<IReadOnlyList<WarehouseTask>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch tasks awaiting assignment — those queued or created but not yet assigned to a worker —
    /// so the assignment flow (task 19.1) can attempt to place them with an on-shift worker.
    /// </summary>
    Task<IReadOnlyList<WarehouseTask>> GetUnassignedAsync(CancellationToken ct = default);

    /// <summary>Stage a newly generated task for insertion (Pick / PutAway / Load, tasks 24.1, 24.2).</summary>
    void Add(WarehouseTask task);

    /// <summary>Stage mutations to an existing task (assignment, status, travel time) for update.</summary>
    void Update(WarehouseTask task);
}
