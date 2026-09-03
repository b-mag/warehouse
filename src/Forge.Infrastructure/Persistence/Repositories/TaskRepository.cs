using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Common;
using Forge.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = Forge.Domain.Tasks.TaskStatus;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ITaskRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.WarehouseTasks"/> <see cref="DbSet{TEntity}"/>. Tasks fetched by the
/// assignment / completion handlers are mutated (assignment, status, travel time) and then saved through
/// the same unit of work, so query methods return tracked entities; <see cref="Add"/>/<see cref="Update"/>
/// stage changes that <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> commits atomically.
/// </summary>
public sealed class TaskRepository : ITaskRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public TaskRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<WarehouseTask?> GetByIdAsync(WarehouseTaskId id, CancellationToken ct = default) =>
        _context.WarehouseTasks.FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WarehouseTask>> ListAllAsync(CancellationToken ct = default) =>
        await _context.WarehouseTasks.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WarehouseTask>> GetUnassignedAsync(CancellationToken ct = default) =>
        // "Awaiting assignment" = created-but-not-yet-assigned or explicitly queued for an available
        // worker (Req 8.2, 8.3). Assigned/InProgress/Completed tasks are already placed or done.
        await _context.WarehouseTasks
            .Where(t => t.Status == DomainTaskStatus.Created || t.Status == DomainTaskStatus.Queued)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(WarehouseTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _context.WarehouseTasks.Add(task);
    }

    /// <inheritdoc />
    public void Update(WarehouseTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        _context.WarehouseTasks.Update(task);
    }
}
