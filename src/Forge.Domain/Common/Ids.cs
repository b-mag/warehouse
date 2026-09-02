namespace Forge.Domain.Common;

// Strongly-typed identifiers (Req 3.1).
//
// Each id is a readonly record struct wrapping a Guid so the domain never
// confuses one kind of id for another. They implement IComparable<T> so the
// design's ascending-id tie-breaks (FEFO selection, reservation contention,
// slotting) are well-defined and deterministic. Ordering delegates to
// Guid.CompareTo, which is a total order.
//
// Factory: New() mints a fresh id; the primary-constructor Value carries an
// existing Guid (e.g., when rehydrated from persistence or mapped from a DTO).

/// <summary>Identifier for a gel type / formulation family (Req 3.1).</summary>
public readonly record struct GelTypeId(Guid Value) : IComparable<GelTypeId>
{
    public static GelTypeId New() => new(Guid.NewGuid());
    public int CompareTo(GelTypeId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a gel lot (a produced batch) (Req 3.1, 5.2 tie-break).</summary>
public readonly record struct GelLotId(Guid Value) : IComparable<GelLotId>
{
    public static GelLotId New() => new(Guid.NewGuid());
    public int CompareTo(GelLotId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a temperature zone (Req 3.1, 16 slotting tie-break).</summary>
public readonly record struct ZoneId(Guid Value) : IComparable<ZoneId>
{
    public static ZoneId New() => new(Guid.NewGuid());
    public int CompareTo(ZoneId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a mobile agent (Req 3.1, 19.6 reservation tie-break).</summary>
public readonly record struct AgentId(Guid Value) : IComparable<AgentId>
{
    public static AgentId New() => new(Guid.NewGuid());
    public int CompareTo(AgentId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a labor worker (Req 3.1).</summary>
public readonly record struct WorkerId(Guid Value) : IComparable<WorkerId>
{
    public static WorkerId New() => new(Guid.NewGuid());
    public int CompareTo(WorkerId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a warehouse task (Req 3.1).</summary>
public readonly record struct WarehouseTaskId(Guid Value) : IComparable<WarehouseTaskId>
{
    public static WarehouseTaskId New() => new(Guid.NewGuid());
    public int CompareTo(WarehouseTaskId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a colony (Req 3.1).</summary>
public readonly record struct ColonyId(Guid Value) : IComparable<ColonyId>
{
    public static ColonyId New() => new(Guid.NewGuid());
    public int CompareTo(ColonyId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a colony order (Req 3.1).</summary>
public readonly record struct ColonyOrderId(Guid Value) : IComparable<ColonyOrderId>
{
    public static ColonyOrderId New() => new(Guid.NewGuid());
    public int CompareTo(ColonyOrderId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a starship (Req 3.1).</summary>
public readonly record struct StarshipId(Guid Value) : IComparable<StarshipId>
{
    public static StarshipId New() => new(Guid.NewGuid());
    public int CompareTo(StarshipId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a dock bay (Req 3.1).</summary>
public readonly record struct DockBayId(Guid Value) : IComparable<DockBayId>
{
    public static DockBayId New() => new(Guid.NewGuid());
    public int CompareTo(DockBayId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifier for a pick face (single-occupancy resource) (Req 3.1, 19.4).</summary>
public readonly record struct PickFaceId(Guid Value) : IComparable<PickFaceId>
{
    public static PickFaceId New() => new(Guid.NewGuid());
    public int CompareTo(PickFaceId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString();
}
