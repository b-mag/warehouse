using Forge.Domain.ColdChain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="TemperatureZone"/> (Req 6.1, 26.1). The zone is an entity keyed by its
/// strongly-typed <see cref="TemperatureZone.Id"/>. Its inclusive allowable band is the
/// <see cref="TemperatureRange"/> value object, mapped inline as an owned type; the capacity is stored
/// as-is and <see cref="TemperatureZone.StoredQuantity"/> is written through its private setter by EF.
/// <see cref="TemperatureZone.RemainingCapacity"/> is a computed property and is intentionally not
/// mapped.
/// </summary>
public sealed class TemperatureZoneConfiguration : IEntityTypeConfiguration<TemperatureZone>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TemperatureZone> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("temperature_zones");

        builder.HasKey(z => z.Id);
        builder.Property(z => z.Id)
            .HasConversion(StronglyTypedIdConverters.ZoneId)
            .ValueGeneratedNever();

        builder.Property(z => z.Capacity).IsRequired();
        builder.Property(z => z.StoredQuantity).IsRequired();

        // Do not persist the derived RemainingCapacity (Capacity - StoredQuantity).
        builder.Ignore(z => z.RemainingCapacity);

        // Inclusive allowable temperature band (Req 6.1). The zone's only constructor takes the range as
        // a parameter, so it is stored as a scalar JSON column (via a value converter) which EF can bind
        // to that constructor parameter — an owned/complex mapping cannot be constructor-bound.
        builder.Property(z => z.AllowableRange)
            .HasConversion(ValueObjectConverters.TemperatureRange)
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
