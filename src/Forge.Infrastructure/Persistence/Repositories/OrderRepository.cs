using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.ColonyOrders"/> <see cref="DbSet{TEntity}"/>. Query methods return tracked
/// entities; <see cref="Add"/>/<see cref="Update"/> stage changes that
/// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> commits atomically.
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public OrderRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<ColonyOrder?> GetByIdAsync(ColonyOrderId id, CancellationToken ct = default) =>
        _context.ColonyOrders.FirstOrDefaultAsync(o => o.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColonyOrder>> ListAllAsync(CancellationToken ct = default) =>
        await _context.ColonyOrders.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ColonyOrder>> GetByColonyAsync(ColonyId colonyId, CancellationToken ct = default) =>
        await _context.ColonyOrders
            .Where(o => o.Colony == colonyId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(ColonyOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _context.ColonyOrders.Add(order);
    }

    /// <inheritdoc />
    public void Update(ColonyOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _context.ColonyOrders.Update(order);
    }
}
