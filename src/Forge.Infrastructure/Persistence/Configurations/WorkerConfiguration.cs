using Forge.Domain.Labor;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="Worker"/> (Req 15.1, 26.1). The worker is an entity keyed by its
/// strongly-typed <see cref="Worker.Id"/>, with its hourly rate persisted as a decimal. The worker's
/// shifts (<see cref="Worker.Shifts"/>, a get-only navigation over <see cref="WorkerShift"/> value
/// objects) are mapped as an owned collection through the auto-property backing field so EF can populate
/// the read-only collection without the domain exposing a public mutator.
/// </summary>
public sealed class WorkerConfiguration : IEntityTypeConfiguration<Worker>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Worker> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("workers");

        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id)
            .HasConversion(StronglyTypedIdConverters.WorkerId)
            .ValueGeneratedNever();

        builder.Property(w => w.HourlyRate)
            .HasColumnType("numeric")
            .IsRequired();

        // Shifts are value objects the worker requires as a constructor parameter (Req 15.1; at least one
        // always exists). They are stored as a scalar JSON array via a value converter so EF can bind them
        // to the constructor — an owned child table cannot be constructor-bound.
        builder.Property(w => w.Shifts)
            .HasConversion(ValueObjectConverters.WorkerShifts)
            .Metadata.SetValueComparer(ValueObjectConverters.WorkerShiftsComparer);

        builder.Property(w => w.Shifts)
            .HasColumnType("jsonb")
            .HasColumnName("shifts")
            .IsRequired();
    }
}
