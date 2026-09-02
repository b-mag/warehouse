using Forge.Domain.Common;

namespace Forge.Domain.Docks;

/// <summary>
/// A pick face: a single storage-front location, sited within a temperature
/// <see cref="ZoneId"/>, from which an agent picks gel (Req 18.1). A pick face is a
/// single-occupancy resource — at most one agent uses it at a time (Req 19.4) — so it
/// implements <see cref="ISingleOccupancyResource"/> to give the reservation manager
/// (task 15.2) a key to acquire and queue on.
/// <para>
/// This class models the pick face's identity and its owning zone only; the acquisition /
/// FIFO-queue algorithm lives in the Application layer (task 15.2).
/// </para>
/// </summary>
public sealed class PickFace : ISingleOccupancyResource
{
    /// <summary>Create a pick face sited in the given zone.</summary>
    public PickFace(PickFaceId id, ZoneId zone)
    {
        Id = id;
        Zone = zone;
    }

    /// <summary>This pick face's identity (Req 19.4).</summary>
    public PickFaceId Id { get; }

    /// <summary>The temperature zone this pick face belongs to (Req 19.4).</summary>
    public ZoneId Zone { get; }

    /// <inheritdoc />
    public SingleOccupancyResourceId ResourceId => SingleOccupancyResourceId.ForPickFace(Id);
}
