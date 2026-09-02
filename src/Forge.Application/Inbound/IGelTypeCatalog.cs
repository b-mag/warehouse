using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Application.Inbound;

/// <summary>
/// Resolves a <see cref="GelType"/> (its <see cref="Formulation"/> — storage range, nominal
/// shelf-life — and <see cref="GelType.Velocity"/>) from a <see cref="GelTypeId"/>
/// (design "WMS Core Application abstractions"; Req 11.2, 11.4, 16).
/// <para>
/// <b>Why this abstraction exists.</b> A <see cref="Forge.Application.Abstractions.Commands.RecordInboundGelReceiptCommand"/>
/// carries only the received lot's <see cref="GelTypeId"/> — not the full gel type — because the
/// driver's arrival input speaks ids, not aggregates. But the put-away handler (task 24.2) needs the
/// gel type to (a) derive the lot's expiry from the formulation nominal shelf-life on creation
/// (Req 11.4) and (b) let the active <see cref="Forge.Application.Abstractions.ISlottingStrategy"/>
/// pick a compatible zone by the type's storage range + velocity (Req 11.2, 16). The seeded catalog
/// of 1000 gel types is fixed reference data (Req 25.1), so this read-only lookup is a natural seam:
/// the Application depends only on this abstraction, and the EF Core implementation over the seeded
/// catalog lives in Infrastructure (task 28.2), preserving the layer boundary.
/// </para>
/// </summary>
public interface IGelTypeCatalog
{
    /// <summary>
    /// Fetch the gel type for <paramref name="gelTypeId"/>, or <see langword="null"/> when the id is
    /// unknown (an unknown gel type is an invalid inbound receipt — Req 5.5-style rejection).
    /// </summary>
    Task<GelType?> GetByIdAsync(GelTypeId gelTypeId, CancellationToken ct = default);
}
