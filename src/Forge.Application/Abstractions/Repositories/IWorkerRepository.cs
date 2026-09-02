using Forge.Domain.Common;
using Forge.Domain.Labor;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="Worker"/> aggregates (Req 1.4, 9.5). Interface only; the
/// EF Core implementation lives in Infrastructure (task 28.2). Supports the shift-gated worker-assignment
/// flow (task 19.1), which needs the pool of workers on shift at the current simulated time.
/// </summary>
public interface IWorkerRepository
{
    /// <summary>Fetch a worker by its identity, or <c>null</c> if none exists.</summary>
    Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken ct = default);

    /// <summary>Fetch every worker (used by labor metrics and snapshot queries).</summary>
    Task<IReadOnlyList<Worker>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the workers on shift at <paramref name="at"/> (per <see cref="Worker.IsOnShift(DateTimeOffset)"/>),
    /// i.e. the candidates eligible for task assignment at that simulated time (Req 15.5, task 19.1).
    /// </summary>
    Task<IReadOnlyList<Worker>> GetOnShiftAsync(DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Stage a new worker for insertion.</summary>
    void Add(Worker worker);

    /// <summary>Stage mutations to an existing worker for update.</summary>
    void Update(Worker worker);
}
