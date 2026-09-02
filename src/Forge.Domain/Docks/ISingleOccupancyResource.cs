using Forge.Domain.Common;

namespace Forge.Domain.Docks;

/// <summary>
/// A resource that at most one <see cref="AgentId"/> may use at any instant of
/// simulated time (Req 19.4). A <see cref="DockBay"/> and a <see cref="PickFace"/>
/// are the two single-occupancy resources in the domain.
/// <para>
/// This is a domain marker: it exposes a stable, comparable
/// <see cref="ResourceId"/> so the reservation manager (task 15.2) can key
/// acquisitions and the FIFO wait queue on the resource regardless of its concrete
/// kind. The acquisition / queue algorithm itself lives in the Application layer;
/// this interface only guarantees an identity to key on.
/// </para>
/// </summary>
public interface ISingleOccupancyResource
{
    /// <summary>
    /// The resource's kind-agnostic identity. The reservation manager keys the held
    /// grant and the FIFO waiter queue on this value (task 15.2, Req 19.4).
    /// </summary>
    SingleOccupancyResourceId ResourceId { get; }
}

/// <summary>
/// A kind-agnostic key for an <see cref="ISingleOccupancyResource"/> (Req 19.4).
/// <para>
/// A <see cref="DockBayId"/> and a <see cref="PickFaceId"/> are distinct id types, so
/// the reservation manager needs a single comparable key that can hold either without
/// confusing a dock bay for a pick face. This value records the resource
/// <see cref="Kind"/> alongside the underlying <see cref="Value"/> so two resources of
/// different kinds never collide even if their <see cref="Guid"/>s were to coincide.
/// Ordering is total (kind, then guid), giving the reservation manager a deterministic
/// key for dictionaries and tie-breaks.
/// </para>
/// </summary>
public readonly record struct SingleOccupancyResourceId(
    SingleOccupancyResourceKind Kind,
    Guid Value) : IComparable<SingleOccupancyResourceId>
{
    /// <summary>Key a dock bay as a single-occupancy resource.</summary>
    public static SingleOccupancyResourceId ForDockBay(DockBayId id) =>
        new(SingleOccupancyResourceKind.DockBay, id.Value);

    /// <summary>Key a pick face as a single-occupancy resource.</summary>
    public static SingleOccupancyResourceId ForPickFace(PickFaceId id) =>
        new(SingleOccupancyResourceKind.PickFace, id.Value);

    /// <summary>Total order by <see cref="Kind"/> then <see cref="Value"/> (deterministic).</summary>
    public int CompareTo(SingleOccupancyResourceId other)
    {
        int byKind = ((int)Kind).CompareTo((int)other.Kind);
        return byKind != 0 ? byKind : Value.CompareTo(other.Value);
    }

    public override string ToString() => $"{Kind}:{Value}";
}

/// <summary>The concrete kind of a <see cref="SingleOccupancyResourceId"/> (Req 19.4).</summary>
public enum SingleOccupancyResourceKind
{
    /// <summary>A <see cref="DockBay"/>.</summary>
    DockBay = 0,

    /// <summary>A <see cref="PickFace"/>.</summary>
    PickFace = 1,
}
