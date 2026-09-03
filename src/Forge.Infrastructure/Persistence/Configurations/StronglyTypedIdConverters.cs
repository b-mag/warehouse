using Forge.Domain.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core value converters that map the domain's strongly-typed id structs (Req 3.1 — <c>GelTypeId</c>,
/// <c>GelLotId</c>, <c>ZoneId</c>, etc.) to and from the underlying <see cref="Guid"/> column type. The
/// ids are <c>readonly record struct</c>s wrapping a <see cref="Guid"/>; the Domain never references EF
/// Core, so these converters live entirely in Infrastructure and are attached to properties by the
/// per-aggregate <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{TEntity}"/> classes.
/// This lets the DbContext persist a <c>GelLotId</c> as a plain <c>uuid</c> column without leaking any
/// persistence concern into the domain.
/// </summary>
internal static class StronglyTypedIdConverters
{
    /// <summary>Converts a <see cref="GelTypeId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<GelTypeId, Guid> GelTypeId =
        new(id => id.Value, value => new GelTypeId(value));

    /// <summary>Converts a <see cref="GelLotId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<GelLotId, Guid> GelLotId =
        new(id => id.Value, value => new GelLotId(value));

    /// <summary>Converts a <see cref="ZoneId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<ZoneId, Guid> ZoneId =
        new(id => id.Value, value => new ZoneId(value));

    /// <summary>Converts an <see cref="AgentId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<AgentId, Guid> AgentId =
        new(id => id.Value, value => new AgentId(value));

    /// <summary>Converts a <see cref="WorkerId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<WorkerId, Guid> WorkerId =
        new(id => id.Value, value => new WorkerId(value));

    /// <summary>Converts a <see cref="WarehouseTaskId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<WarehouseTaskId, Guid> WarehouseTaskId =
        new(id => id.Value, value => new WarehouseTaskId(value));

    /// <summary>Converts a <see cref="ColonyId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<ColonyId, Guid> ColonyId =
        new(id => id.Value, value => new ColonyId(value));

    /// <summary>Converts a <see cref="ColonyOrderId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<ColonyOrderId, Guid> ColonyOrderId =
        new(id => id.Value, value => new ColonyOrderId(value));

    /// <summary>Converts a <see cref="StarshipId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<StarshipId, Guid> StarshipId =
        new(id => id.Value, value => new StarshipId(value));

    /// <summary>Converts a <see cref="DockBayId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<DockBayId, Guid> DockBayId =
        new(id => id.Value, value => new DockBayId(value));

    /// <summary>Converts a <see cref="PickFaceId"/> to/from its underlying <see cref="Guid"/>.</summary>
    public static readonly ValueConverter<PickFaceId, Guid> PickFaceId =
        new(id => id.Value, value => new PickFaceId(value));

    /// <summary>Nullable <see cref="ZoneId"/> converter for optional assigned-zone references.</summary>
    public static readonly ValueConverter<ZoneId?, Guid?> NullableZoneId =
        new(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            value => value.HasValue ? new ZoneId(value.Value) : (ZoneId?)null);

    /// <summary>Nullable <see cref="WorkerId"/> converter for an unassigned task's worker reference.</summary>
    public static readonly ValueConverter<WorkerId?, Guid?> NullableWorkerId =
        new(
            id => id.HasValue ? id.Value.Value : (Guid?)null,
            value => value.HasValue ? new WorkerId(value.Value) : (WorkerId?)null);
}
