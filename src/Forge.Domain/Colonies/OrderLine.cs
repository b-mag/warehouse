namespace Forge.Domain.Colonies;

using Forge.Domain.Common;

/// <summary>
/// A single line of a <see cref="ColonyOrder"/>: a requested <see cref="Quantity"/> of a
/// <see cref="GelType"/> (Req 12.1). Pure data.
/// <para>
/// <b>Validation placement.</b> The data type is kept clean — the positional record itself imposes
/// no quantity constraint so it can freely represent orders during construction, mapping, and
/// persistence rehydration. The business rule "quantity must be &gt;= 1" is enforced at the edge:
/// the <see cref="CreateColonyOrder"/> use-case handler (task 24.1) validates it, and the optional
/// <see cref="Create"/> factory here offers the same check for callers that prefer to validate at
/// construction time. Keeping the invariant out of the type avoids duplicating (and risking
/// divergence of) the rule the handler already owns.
/// </para>
/// </summary>
/// <param name="GelType">The gel type requested on this line.</param>
/// <param name="Quantity">The requested quantity (business rule: &gt;= 1, enforced at the edge).</param>
public sealed record OrderLine(GelTypeId GelType, int Quantity)
{
    /// <summary>
    /// Optional validated construction: rejects a <paramref name="quantity"/> below 1 with a
    /// <see cref="DomainError.Validation"/> naming the attribute; otherwise returns the line. Callers
    /// that build order lines directly may skip this and rely on the create-order handler's
    /// validation instead.
    /// </summary>
    public static Result<OrderLine> Create(GelTypeId gelType, int quantity)
    {
        if (quantity < 1)
        {
            return DomainError.Validation(
                $"Order line quantity must be at least 1 but was {quantity}.",
                nameof(Quantity));
        }

        return new OrderLine(gelType, quantity);
    }
}
