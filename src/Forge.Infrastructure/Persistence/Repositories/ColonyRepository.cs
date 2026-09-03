using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IColonyRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.Colonies"/> <see cref="DbSet{TEntity}"/>. Query methods return tracked
/// entities; <see cref="Add"/>/<see cref="Update"/> stage changes that
/// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> commits atomically.
/// </summary>
public sealed class ColonyRepository : IColonyRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public ColonyRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<Colony?> GetByIdAsync(ColonyId id, CancellationToken ct = default) =>
        _context.Colonies.FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Colony>> ListAllAsync(CancellationToken ct = default) =>
        await _context.Colonies.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(Colony colony)
    {
        ArgumentNullException.ThrowIfNull(colony);
        _context.Colonies.Add(colony);
    }

    /// <inheritdoc />
    public void Update(Colony colony)
    {
        ArgumentNullException.ThrowIfNull(colony);
        _context.Colonies.Update(colony);
    }
}
