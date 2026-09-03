using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IGelLotRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.GelLots"/> <see cref="DbSet{TEntity}"/>: query methods run against the set
/// and stage nothing, while <see cref="Add"/>/<see cref="Update"/> stage inserts/updates that the shared
/// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> commits atomically.
/// <para>
/// Queries return <b>tracked</b> entities. Lots fetched here are routinely mutated by the rule handlers
/// (expiry decay across every lot, at-risk/quantity/zone changes) and then persisted through the same
/// context in the same unit of work, so tracking is required for those changes to be detected. The
/// snapshot/query handlers that only read do so through the same tracked reads without harm.
/// </para>
/// </summary>
public sealed class GelLotRepository : IGelLotRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public GelLotRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<GelLot?> GetByIdAsync(GelLotId id, CancellationToken ct = default) =>
        _context.GelLots.FirstOrDefaultAsync(l => l.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GelLot>> GetByGelTypeAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
        await _context.GelLots
            .Where(l => l.GelTypeId == gelTypeId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<GelLot>> ListAllAsync(CancellationToken ct = default) =>
        await _context.GelLots.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(GelLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        _context.GelLots.Add(lot);
    }

    /// <inheritdoc />
    public void Update(GelLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        _context.GelLots.Update(lot);
    }
}
