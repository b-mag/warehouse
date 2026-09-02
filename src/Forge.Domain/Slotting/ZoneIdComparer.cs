using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Domain.Slotting;

/// <summary>
/// The ascending-<see cref="ZoneId"/> tie-break primitive shared by every slotting strategy
/// (Req 16.2, Property 9). When multiple compatible zones qualify for a gel lot, selection ties
/// break by ascending zone identifier, so identical inputs deterministically yield an identical
/// selection (Req 16.5).
/// <para>
/// This is the neutral ordering primitive strategies build on. <c>NaiveFirstAvailableStrategy</c>
/// orders candidates by this comparer and takes the first; <c>VelocityAffinitySlottingStrategy</c>
/// (Application task 18.1) applies its travel-time preference first and uses this comparer only to
/// break ties. The comparer itself carries no velocity/affinity logic — it is a total order on
/// <see cref="ZoneId"/> (delegating to <see cref="ZoneId.CompareTo(ZoneId)"/>, which is itself a
/// total order over the underlying Guid).
/// </para>
/// <para>
/// A singleton <see cref="Instance"/> is exposed so callers reuse one stateless comparer.
/// </para>
/// </summary>
public sealed class ZoneIdComparer : IComparer<TemperatureZone>, IComparer<ZoneId>
{
    /// <summary>The shared stateless instance of the comparer.</summary>
    public static ZoneIdComparer Instance { get; } = new();

    private ZoneIdComparer()
    {
    }

    /// <summary>
    /// Orders two zones by ascending <see cref="TemperatureZone.Id"/> (Req 16.2). Nulls sort
    /// before non-nulls so the comparer never throws when handed a sparse collection.
    /// </summary>
    public int Compare(TemperatureZone? x, TemperatureZone? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        return x.Id.CompareTo(y.Id);
    }

    /// <summary>Orders two zone identifiers ascending (Req 16.2), the underlying tie-break key.</summary>
    public int Compare(ZoneId x, ZoneId y) => x.CompareTo(y);
}
