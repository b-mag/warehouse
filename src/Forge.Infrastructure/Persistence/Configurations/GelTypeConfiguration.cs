using Forge.Domain.ColdChain;
using Forge.Domain.Gels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="GelType"/> (Req 3.2, 26.1). The gel type is an entity keyed by its
/// strongly-typed <see cref="GelType.Id"/>; its shared recipe is the <see cref="Formulation"/> value
/// object, mapped as an owned type so it persists inline on the gel-type row. The nested
/// <see cref="TemperatureRange"/> storage band is likewise owned, and the flavor list is mapped to a
/// Postgres <c>text[]</c> array column (a primitive collection). Everything here is a persistence
/// concern kept out of the Domain: the domain type exposes only private setters / init-only members and
/// no EF attributes.
/// </summary>
public sealed class GelTypeConfiguration : IEntityTypeConfiguration<GelType>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GelType> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gel_types");

        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id)
            .HasConversion(StronglyTypedIdConverters.GelTypeId)
            .ValueGeneratedNever();

        builder.Property(g => g.Velocity)
            .IsRequired();

        // Formulation is the value object shared by every lot of this type (Req 3.2, 3.3). The gel type's
        // only constructor takes the formulation as a parameter, so it is stored as a single scalar JSON
        // (jsonb) column via a value converter — an owned/complex mapping cannot be bound to a constructor
        // parameter. The converter captures the storage range, nominal shelf-life, and flavor list, so the
        // whole recipe round-trips losslessly (Req 16.1, 25.1).
        builder.Property(g => g.Formulation)
            .HasConversion(ValueObjectConverters.Formulation)
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
