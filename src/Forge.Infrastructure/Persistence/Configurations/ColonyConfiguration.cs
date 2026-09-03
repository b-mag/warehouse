using Forge.Domain.Colonies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Colony"/> (Req 12.1, 26.1). The colony is an entity keyed by its
/// strongly-typed <see cref="Colony.Id"/>; its <see cref="DemandProfile"/> is a pure-data value object the
/// colony requires as a constructor parameter. Because the profile carries a strongly-typed-id-keyed
/// base-rate map and ordered trend boundaries — neither of which EF can bind to a constructor as an owned
/// relation — the whole profile is persisted as a single JSON (<c>jsonb</c>) column via a value converter.
/// This keeps the mapping in Infrastructure and leaves the pure-data <see cref="DemandProfile"/> unchanged.
/// </summary>
public sealed class ColonyConfiguration : IEntityTypeConfiguration<Colony>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Colony> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("colonies");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id)
            .HasConversion(StronglyTypedIdConverters.ColonyId)
            .ValueGeneratedNever();

        // DemandProfile is pure value data the colony requires in its constructor (Req 12.1, 12.3, 12.6),
        // persisted as a single jsonb column via a value converter so EF can bind it to the constructor.
        builder.Property(c => c.Profile)
            .HasConversion(ValueObjectConverters.DemandProfile)
            .HasColumnType("jsonb")
            .HasColumnName("demand_profile")
            .IsRequired();
    }
}
