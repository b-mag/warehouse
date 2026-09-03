using Forge.Domain.Docks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="PickFace"/> (Req 19.4, 26.1). The pick face is an entity keyed by its
/// strongly-typed <see cref="PickFace.Id"/> and references its owning temperature
/// <see cref="PickFace.Zone"/> via a strongly-typed zone id (mapped through the shared converter). The
/// computed <see cref="PickFace.ResourceId"/> single-occupancy key is not persisted.
/// </summary>
public sealed class PickFaceConfiguration : IEntityTypeConfiguration<PickFace>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PickFace> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("pick_faces");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasConversion(StronglyTypedIdConverters.PickFaceId)
            .ValueGeneratedNever();

        builder.Property(p => p.Zone)
            .HasConversion(StronglyTypedIdConverters.ZoneId)
            .IsRequired();

        builder.Ignore(p => p.ResourceId);
    }
}
