using Forge.Domain.ColdChain;

namespace Forge.Domain.Gels;

/// <summary>
/// The immutable recipe/spec shared by every lot of a gel type (Req 3.2, 3.3).
/// A value object that compares equal exactly when all of its attributes are equal.
/// <para>
/// It carries the required <see cref="StorageRange"/> (the temperature band a compatible
/// zone must contain — Req 16.1), the <see cref="NominalShelfLife"/> used to derive a lot's
/// expiry timestamp on creation (Req 3.4, 11.4), and one or more <see cref="Flavors"/>.
/// </para>
/// <para>
/// <b>Structural equality of <see cref="Flavors"/>.</b> C# records generate member-wise
/// equality, but for reference-typed members (like <see cref="IReadOnlyList{T}"/>) that member
/// equality is <em>reference</em> equality — two distinct lists with identical contents would
/// otherwise compare unequal, breaking the "equal when all attributes equal" contract (Req 3.3).
/// To honor value semantics, <see cref="Equals(Formulation)"/> and <see cref="GetHashCode"/>
/// are overridden to compare the flavor sequences element-by-element (order-sensitive
/// <see cref="System.Linq.Enumerable.SequenceEqual{TSource}(IEnumerable{TSource}, IEnumerable{TSource})"/>-style
/// comparison, implemented here BCL-only) while the value-typed <see cref="StorageRange"/> and
/// <see cref="NominalShelfLife"/> compare by value automatically.
/// </para>
/// </summary>
/// <param name="StorageRange">The required storage temperature band (Req 16.1).</param>
/// <param name="NominalShelfLife">Nominal shelf-life; drives lot expiry (Req 3.4, 11.4). Seeded 1..365 days (Req 25.1).</param>
/// <param name="Flavors">One or more flavor descriptors (Req 25.1 requires ≥ 1 when seeded).</param>
public sealed record Formulation(
    TemperatureRange StorageRange,
    TimeSpan NominalShelfLife,
    IReadOnlyList<string> Flavors)
{
    /// <summary>
    /// Value equality including the <see cref="Flavors"/> contents (Req 3.3). Two formulations are
    /// equal when their storage range and nominal shelf-life are equal and their flavor sequences
    /// contain the same elements in the same order.
    /// </summary>
    public bool Equals(Formulation? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return StorageRange.Equals(other.StorageRange)
            && NominalShelfLife.Equals(other.NominalShelfLife)
            && FlavorsEqual(Flavors, other.Flavors);
    }

    /// <summary>Hash consistent with <see cref="Equals(Formulation)"/>: folds the flavor contents in order.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StorageRange);
        hash.Add(NominalShelfLife);

        // Fold each flavor in order so that equal sequences yield equal hashes.
        foreach (var flavor in Flavors)
        {
            hash.Add(flavor);
        }

        return hash.ToHashCode();
    }

    /// <summary>Order-sensitive element-by-element comparison of two flavor sequences (BCL-only).</summary>
    private static bool FlavorsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
