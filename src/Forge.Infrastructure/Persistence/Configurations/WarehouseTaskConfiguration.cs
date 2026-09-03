using Forge.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="WarehouseTask"/> (Req 8.1, 26.1). The task is an entity keyed by its
/// strongly-typed <see cref="WarehouseTask.Id"/>. Its <see cref="WarehouseTask.Type"/> and
/// <see cref="WarehouseTask.Status"/> enums are stored as strings for readability/stability, its two
/// grid <see cref="Forge.Domain.Spatial.Cell"/> endpoints are mapped inline as owned value objects, and
/// the optional <see cref="WarehouseTask.AssignedWorker"/> maps through the nullable worker-id converter.
/// The rule-driven <see cref="WarehouseTask.TravelTime"/> / <see cref="WarehouseTask.Status"/> /
/// <see cref="WarehouseTask.AssignedWorker"/> members have private setters that EF writes through, so the
/// mapping never widens the domain's guarded lifecycle.
/// </summary>
public sealed class WarehouseTaskConfiguration : IEntityTypeConfiguration<WarehouseTask>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<WarehouseTask> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("warehouse_tasks");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id)
            .HasConversion(StronglyTypedIdConverters.WarehouseTaskId)
            .ValueGeneratedNever();

        builder.Property(t => t.Type)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(t => t.Status)
            .HasConversion<string>()
            .IsRequired();

        builder.Property(t => t.EstimatedDuration).IsRequired();
        builder.Property(t => t.TravelTime).IsRequired();

        builder.Property(t => t.AssignedWorker)
            .HasConversion(StronglyTypedIdConverters.NullableWorkerId);

        // Origin and destination are Cell value objects (integer grid coordinates, Req 18.1). The task's
        // only constructor takes both cells as parameters, so each is stored as a scalar JSON column via a
        // value converter that EF can bind to the constructor — an owned/complex mapping cannot be bound.
        builder.Property(t => t.Origin)
            .HasConversion(ValueObjectConverters.Cell)
            .HasColumnName("origin")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(t => t.Destination)
            .HasConversion(ValueObjectConverters.Cell)
            .HasColumnName("destination")
            .HasColumnType("jsonb")
            .IsRequired();
    }
}
