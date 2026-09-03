using Forge.Domain.ColdChain;
using Forge.Domain.Gels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Forge.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core mapping for <see cref="GelLot"/> (Req 3.1, 26.1). The lot is an entity keyed by its
/// strongly-typed <see cref="GelLot.Id"/>. Its mutable rule-driven state (<see cref="GelLot.Quantity"/>,
/// <see cref="GelLot.IsExpired"/>, <see cref="GelLot.AtRisk"/>, <see cref="GelLot.AssignedZoneId"/>) has
/// private setters, and EF Core writes through those via the CLR members — persistence never widens the
/// domain's encapsulation. The strongly-typed <see cref="GelLot.GelTypeId"/> and the optional
/// <see cref="GelLot.AssignedZoneId"/> map through the shared id converters, and the timestamp-ordered
/// temperature history (the private <c>_history</c> list surfaced as <see cref="GelLot.TemperatureHistory"/>)
/// is mapped as an owned collection of <see cref="TemperatureReading"/> value objects using field access
/// so the read-only navigation is honored.
/// </summary>
public sealed class GelLotConfiguration : IEntityTypeConfiguration<GelLot>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<GelLot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("gel_lots");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id)
            .HasConversion(StronglyTypedIdConverters.GelLotId)
            .ValueGeneratedNever();

        builder.Property(l => l.GelTypeId)
            .HasConversion(StronglyTypedIdConverters.GelTypeId)
            .IsRequired();

        builder.Property(l => l.ProducedAt).IsRequired();
        builder.Property(l => l.ExpiresAt).IsRequired();
        builder.Property(l => l.Quantity).IsRequired();
        builder.Property(l => l.FefoPriority).IsRequired();
        builder.Property(l => l.IsExpired).IsRequired();
        builder.Property(l => l.AtRisk).IsRequired();

        // Optional assigned-zone reference (a zone-less lot cannot record readings, Req 6.4).
        builder.Property(l => l.AssignedZoneId)
            .HasConversion(StronglyTypedIdConverters.NullableZoneId);

        // Index the FEFO ordering keys so selection queries (expiry, priority) can be served efficiently.
        builder.HasIndex(l => new { l.GelTypeId, l.ExpiresAt });

        // Temperature history is a value-object collection kept ordered by timestamp (Req 6.2). The lot
        // has no constructor parameter for it; readings accumulate in the private _history List field. The
        // backing field is mapped directly (its CLR type is List<TemperatureReading>) and stored as a
        // scalar JSON array via a value converter — an owned child table is avoided. The read-only
        // TemperatureHistory navigation surfaces the same field and is not double-mapped.
        builder.Property<List<TemperatureReading>>("_history")
            .HasConversion(ValueObjectConverters.TemperatureReadings)
            .Metadata.SetValueComparer(ValueObjectConverters.TemperatureReadingsComparer);

        builder.Property<List<TemperatureReading>>("_history")
            .HasColumnType("jsonb")
            .HasColumnName("temperature_history")
            .IsRequired();

        builder.Ignore(l => l.TemperatureHistory);
    }
}
