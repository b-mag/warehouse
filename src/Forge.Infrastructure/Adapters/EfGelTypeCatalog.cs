using Forge.Application.Inbound;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// The EF Core implementation of the Application <see cref="IGelTypeCatalog"/> seam (task 33.3;
/// Req 11.2, 11.4, 16). It resolves a <see cref="GelType"/> from its id against the seeded catalog of
/// 1000 gel types persisted in <see cref="ForgeDbContext.GelTypes"/> (Req 25.1), so the inbound
/// put-away handler can derive a received lot's expiry from the formulation's nominal shelf-life and
/// let the active slotting strategy pick a compatible zone.
/// <para>
/// It is a thin, read-only adapter over the scoped <see cref="ForgeDbContext"/> — registered scoped
/// alongside the repositories so it participates in the same per-operation DI scope the gateway opens.
/// An unknown id returns <see langword="null"/> (an invalid inbound receipt, rejected by the handler).
/// </para>
/// </summary>
public sealed class EfGelTypeCatalog : IGelTypeCatalog
{
    private readonly ForgeDbContext _context;

    /// <summary>Create the catalog over the supplied scoped <paramref name="context"/>.</summary>
    public EfGelTypeCatalog(ForgeDbContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public Task<GelType?> GetByIdAsync(GelTypeId gelTypeId, CancellationToken ct = default) =>
        _context.GelTypes.FirstOrDefaultAsync(g => g.Id == gelTypeId, ct);
}
