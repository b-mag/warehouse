using Forge.Application.Abstractions.Repositories;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/> (Req 9.5, 26.2). It is the transactional boundary
/// shared by every repository: because all repositories are constructed over the same scoped
/// <see cref="ForgeDbContext"/>, the inserts/updates they stage through <c>Add</c>/<c>Update</c> are
/// committed together here. <see cref="SaveChangesAsync(CancellationToken)"/> delegates straight to
/// <see cref="ForgeDbContext.SaveChangesAsync(CancellationToken)"/>, which persists all tracked changes
/// as a single atomic operation and returns the number of state entries written to the database.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the unit of work over the supplied scoped <paramref name="context"/>.</summary>
    public UnitOfWork(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken ct = default) =>
        _context.SaveChangesAsync(ct);
}
