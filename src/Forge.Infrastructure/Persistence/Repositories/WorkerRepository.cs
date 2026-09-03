using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Common;
using Forge.Domain.Labor;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWorkerRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.Workers"/> <see cref="DbSet{TEntity}"/>. Query methods return tracked
/// entities; <see cref="Add"/>/<see cref="Update"/> stage changes that
/// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> commits atomically.
/// </summary>
public sealed class WorkerRepository : IWorkerRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public WorkerRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<Worker?> GetByIdAsync(WorkerId id, CancellationToken ct = default) =>
        _context.Workers.FirstOrDefaultAsync(w => w.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Worker>> ListAllAsync(CancellationToken ct = default) =>
        await _context.Workers.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Worker>> GetOnShiftAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        // A worker's shifts are persisted as a single value-converted (JSON) column, so the
        // IsOnShift(at) predicate cannot be translated to SQL. Materialize the workers, then apply the
        // pure domain predicate in memory (Req 15.5, task 19.1). The worker set is small (operator-set
        // headcount), so this is inexpensive.
        var workers = await _context.Workers.ToListAsync(ct).ConfigureAwait(false);
        return workers.Where(w => w.IsOnShift(at)).ToList();
    }

    /// <inheritdoc />
    public void Add(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _context.Workers.Add(worker);
    }

    /// <inheritdoc />
    public void Update(Worker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _context.Workers.Update(worker);
    }
}
