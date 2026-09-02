namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Transactional boundary for the repositories (Req 1.4, 9.5). Interface only; the EF Core
/// implementation lives in Infrastructure (task 28.2). Handlers stage changes through the
/// repositories' <c>Add</c>/<c>Update</c> methods, then commit them atomically here.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Persist all staged changes as a single unit of work and return the number of state entries written.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
