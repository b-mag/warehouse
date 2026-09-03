using Forge.Domain.Docks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="DockBay"/> (Req 17.1, 26.1). The bay is an entity keyed by its
/// strongly-typed <see cref="DockBay.Id"/> with an <see cref="DockBay.IsOpen"/> flag. Its immutable
/// <see cref="DockSchedule"/> is required by the bay's constructor and is not something EF can bind as an
/// owned relation, so it is persisted as a single JSON (<c>jsonb</c>) column via a value converter that
/// serializes the schedule's slots and rebuilds it through the schedule constructor. The computed
/// <see cref="DockBay.ResourceId"/> single-occupancy key is not persisted.
/// </summary>
public sealed class DockBayConfiguration : IEntityTypeConfiguration<DockBay>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DockBay> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("dock_bays");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id)
            .HasConversion(StronglyTypedIdConverters.DockBayId)
            .ValueGeneratedNever();

        builder.Property(b => b.IsOpen).IsRequired();

        // The single-occupancy resource key is derived from the id and is not stored.
        builder.Ignore(b => b.ResourceId);

        // The immutable schedule is required by the bay's constructor; persist it as a jsonb column via a
        // value converter (its slots serialized, rebuilt through the schedule constructor) (Req 17.1).
        builder.Property(b => b.Schedule)
            .HasConversion(ValueObjectConverters.DockSchedule)
            .Metadata.SetValueComparer(ValueObjectConverters.DockScheduleComparer);

        builder.Property(b => b.Schedule)
            .HasColumnType("jsonb")
            .HasColumnName("schedule")
            .IsRequired();
    }
}
