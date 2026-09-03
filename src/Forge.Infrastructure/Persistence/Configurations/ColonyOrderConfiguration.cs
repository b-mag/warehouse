using Forge.Domain.Colonies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="ColonyOrder"/> (Req 12.1, 26.1). The order is an entity keyed by its
/// strongly-typed <see cref="ColonyOrder.Id"/> and references the placing colony via a strongly-typed
/// <see cref="ColonyOrder.Colony"/> id (mapped through the shared converter). Its request lines are the
/// <see cref="OrderLine"/> value objects, mapped as an owned collection so they persist as child rows of
/// the order.
/// </summary>
public sealed class ColonyOrderConfiguration : IEntityTypeConfiguration<ColonyOrder>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ColonyOrder> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("colony_orders");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id)
            .HasConversion(StronglyTypedIdConverters.ColonyOrderId)
            .ValueGeneratedNever();

        builder.Property(o => o.Colony)
            .HasConversion(StronglyTypedIdConverters.ColonyId)
            .IsRequired();

        builder.Property(o => o.DeliveryWindowStart).IsRequired();
        builder.Property(o => o.DeliveryWindowEnd).IsRequired();

        // The requested lines are value objects the order record requires as a constructor parameter
        // (Req 12.1). They are stored as a scalar JSON array via a value converter so EF can bind them to
        // the record constructor — an owned child table cannot be constructor-bound.
        builder.Property(o => o.Lines)
            .HasConversion(ValueObjectConverters.OrderLines)
            .Metadata.SetValueComparer(ValueObjectConverters.OrderLinesComparer);

        builder.Property(o => o.Lines)
            .HasColumnType("jsonb")
            .HasColumnName("lines")
            .IsRequired();
    }
}
