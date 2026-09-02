using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Application.Abstractions.Repositories;

/// <summary>
/// Persistence abstraction for <see cref="GelLot"/> aggregates (Req 1.4, 9.5). Defined in the
/// Application as an interface only; the EF Core implementation lives in Infrastructure (task 28.2).
/// Query methods are async and cancellable; <see cref="Add"/> and <see cref="Update"/> stage changes
/// that are committed atomically through <see cref="IUnitOfWork.SaveChangesAsync(CancellationToken)"/>.
/// </summary>
public interface IGelLotRepository
{
    /// <summary>Fetch a lot by its identity, or <c>null</c> if none exists.</summary>
    Task<GelLot?> GetByIdAsync(GelLotId id, CancellationToken ct = default);

    /// <summary>Fetch all lots belonging to a given gel type / formulation family (used by FEFO selection).</summary>
    Task<IReadOnlyList<GelLot>> GetByGelTypeAsync(GelTypeId gelTypeId, CancellationToken ct = default);

    /// <summary>Fetch every lot (used by the per-tick expiry-decay pass and snapshot queries).</summary>
    Task<IReadOnlyList<GelLot>> ListAllAsync(CancellationToken ct = default);

    /// <summary>Stage a newly received lot for insertion (e.g. inbound put-away, task 24.2).</summary>
    void Add(GelLot lot);

    /// <summary>Stage mutations to an existing lot (expiry, at-risk, quantity, zone assignment) for update.</summary>
    void Update(GelLot lot);
}
