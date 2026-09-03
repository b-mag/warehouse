using Forge.Domain.Vessels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Starship"/> (Req 13.1, 26.1). The starship is an entity keyed by its
/// strongly-typed <see cref="Starship.Id"/>, referencing its destination colony via a strongly-typed
/// <see cref="Starship.Destination"/> id. <see cref="Starship.LoadedQuantity"/> is written through its
/// private setter by EF, and <see cref="Starship.RemainingCapacity"/> (a computed property) is not
/// mapped. The scheduled loading windows (the private <c>_windows</c> list surfaced as
/// <see cref="Starship.Windows"/>) are mapped as an owned collection of <see cref="LoadingWindow"/> value
/// objects through the backing field so the read-only navigation is honored.
/// </summary>
public sealed class StarshipConfiguration : IEntityTypeConfiguration<Starship>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Starship> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("starships");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id)
            .HasConversion(StronglyTypedIdConverters.StarshipId)
            .ValueGeneratedNever();

        builder.Property(s => s.CargoCapacity).IsRequired();
        builder.Property(s => s.LoadedQuantity).IsRequired();

        builder.Property(s => s.Destination)
            .HasConversion(StronglyTypedIdConverters.ColonyId)
            .IsRequired();

        builder.Ignore(s => s.RemainingCapacity);

        // Loading windows are value objects the starship requires as a constructor parameter (Req 13.1;
        // at least one always exists). The backing List<LoadingWindow> field is mapped directly (rather
        // than the read-only Windows navigation) so its CLR type matches the constructor parameter, and it
        // is stored as a scalar JSON array via a value converter — an owned child table cannot be bound to
        // a constructor.
        builder.Property<List<LoadingWindow>>("_windows")
            .HasConversion(ValueObjectConverters.LoadingWindows)
            .Metadata.SetValueComparer(ValueObjectConverters.LoadingWindowsComparer);

        builder.Property<List<LoadingWindow>>("_windows")
            .HasColumnType("jsonb")
            .HasColumnName("loading_windows")
            .IsRequired();

        // The read-only Windows navigation surfaces the same field; do not double-map it.
        builder.Ignore(s => s.Windows);
    }
}
