using Forge.Application.Abstractions.Repositories;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IZoneRepository"/> (Req 9.5, 26.2). A thin adapter over the
/// <see cref="ForgeDbContext.TemperatureZones"/> <see cref="DbSet{TEntity}"/>. Zones resolved for
/// slotting/put-away have their stored-quantity mutated and then saved through the same unit of work,
/// so query methods return tracked entities; <see cref="Add"/>/<see cref="Update"/> stage changes for
/// <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/> to commit.
/// </summary>
public sealed class ZoneRepository : IZoneRepository
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the repository over the supplied <paramref name="context"/>.</summary>
    public ZoneRepository(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<TemperatureZone?> GetByIdAsync(ZoneId id, CancellationToken ct = default) =>
        _context.TemperatureZones.FirstOrDefaultAsync(z => z.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TemperatureZone>> ListAllAsync(CancellationToken ct = default) =>
        await _context.TemperatureZones.ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(TemperatureZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _context.TemperatureZones.Add(zone);
    }

    /// <inheritdoc />
    public void Update(TemperatureZone zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        _context.TemperatureZones.Update(zone);
    }
}
