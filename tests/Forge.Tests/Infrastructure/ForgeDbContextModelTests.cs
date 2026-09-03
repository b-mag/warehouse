using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Gels;
using Forge.Domain.Labor;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;
using Forge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Model-building tests for <see cref="ForgeDbContext"/> (task 28.1, Req 26.1). These build the EF Core
/// model with the Npgsql provider and assert the domain aggregates map with the correct keys and that the
/// strongly-typed id keys carry value converters — all WITHOUT a running Postgres. Constructing the model
/// (<see cref="DbContext.Model"/>) exercises every <c>IEntityTypeConfiguration&lt;T&gt;</c> and the value
/// converters for strongly-typed ids and value objects; it does not open a connection.
/// </summary>
public sealed class ForgeDbContextModelTests
{
    // A syntactically-valid Npgsql connection string. UseNpgsql only records provider options; building
    // the model never connects, so no Postgres is required (integration is task 28.3).
    private const string DummyConnectionString =
        "Host=localhost;Port=5432;Database=forge_test;Username=forge;Password=forge";

    private static ForgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseNpgsql(DummyConnectionString)
            .Options;

        return new ForgeDbContext(options);
    }

    [Fact]
    public void Model_builds_without_error()
    {
        using var context = CreateContext();

        // Forcing the model to materialize runs all configurations + converters. A mapping mistake throws here.
        var model = context.Model;

        Assert.NotNull(model);
    }

    [Theory]
    [InlineData(typeof(GelType))]
    [InlineData(typeof(GelLot))]
    [InlineData(typeof(TemperatureZone))]
    [InlineData(typeof(Colony))]
    [InlineData(typeof(ColonyOrder))]
    [InlineData(typeof(Starship))]
    [InlineData(typeof(Worker))]
    [InlineData(typeof(DockBay))]
    [InlineData(typeof(PickFace))]
    [InlineData(typeof(WarehouseTask))]
    public void Every_expected_aggregate_is_mapped_as_an_entity(Type aggregate)
    {
        using var context = CreateContext();

        var entityType = context.Model.FindEntityType(aggregate);

        Assert.NotNull(entityType);
    }

    [Theory]
    [InlineData(typeof(GelType), nameof(GelType.Id))]
    [InlineData(typeof(GelLot), nameof(GelLot.Id))]
    [InlineData(typeof(TemperatureZone), nameof(TemperatureZone.Id))]
    [InlineData(typeof(Colony), nameof(Colony.Id))]
    [InlineData(typeof(ColonyOrder), nameof(ColonyOrder.Id))]
    [InlineData(typeof(Starship), nameof(Starship.Id))]
    [InlineData(typeof(Worker), nameof(Worker.Id))]
    [InlineData(typeof(DockBay), nameof(DockBay.Id))]
    [InlineData(typeof(PickFace), nameof(PickFace.Id))]
    [InlineData(typeof(WarehouseTask), nameof(WarehouseTask.Id))]
    public void Each_aggregate_key_is_its_strongly_typed_id_with_a_value_converter(Type aggregate, string keyName)
    {
        using var context = CreateContext();

        var entityType = context.Model.FindEntityType(aggregate);
        Assert.NotNull(entityType);

        var key = entityType!.FindPrimaryKey();
        Assert.NotNull(key);

        // The primary key is the aggregate's strongly-typed Id property.
        Assert.Single(key!.Properties);
        Assert.Equal(keyName, key.Properties[0].Name);

        // The strongly-typed id struct is stored via a value converter to its underlying Guid.
        var converter = key.Properties[0].GetValueConverter();
        Assert.NotNull(converter);
        Assert.Equal(typeof(Guid), converter!.ProviderClrType);
    }

    [Fact]
    public void GelLot_gel_type_reference_uses_the_gel_type_id_converter()
    {
        using var context = CreateContext();

        var lot = context.Model.FindEntityType(typeof(GelLot));
        Assert.NotNull(lot);

        var gelTypeIdProp = lot!.FindProperty(nameof(GelLot.GelTypeId));
        Assert.NotNull(gelTypeIdProp);

        var converter = gelTypeIdProp!.GetValueConverter();
        Assert.NotNull(converter);
        Assert.Equal(typeof(GelTypeId), converter!.ModelClrType);
        Assert.Equal(typeof(Guid), converter.ProviderClrType);
    }

    [Fact]
    public void GelLot_assigned_zone_is_a_nullable_strongly_typed_id()
    {
        using var context = CreateContext();

        var lot = context.Model.FindEntityType(typeof(GelLot));
        var assignedZone = lot!.FindProperty(nameof(GelLot.AssignedZoneId));

        Assert.NotNull(assignedZone);
        Assert.True(assignedZone!.IsNullable);
        Assert.NotNull(assignedZone.GetValueConverter());
    }

    [Fact]
    public void GelLot_temperature_history_is_a_mapped_property_with_a_value_converter()
    {
        using var context = CreateContext();

        var lot = context.Model.FindEntityType(typeof(GelLot));

        // History is persisted through the backing "_history" field; the read-only TemperatureHistory
        // navigation is intentionally not mapped.
        var history = lot!.FindProperty("_history");

        Assert.NotNull(history);
        Assert.NotNull(history!.GetValueConverter());
    }

    [Fact]
    public void TemperatureZone_allowable_range_maps_via_a_value_converter()
    {
        using var context = CreateContext();

        var zone = context.Model.FindEntityType(typeof(TemperatureZone));
        var range = zone!.FindProperty(nameof(TemperatureZone.AllowableRange));

        Assert.NotNull(range);
        var converter = range!.GetValueConverter();
        Assert.NotNull(converter);
        Assert.Equal(typeof(TemperatureRange), converter!.ModelClrType);
    }

    [Fact]
    public void GelType_formulation_maps_via_a_value_converter()
    {
        using var context = CreateContext();

        var gelType = context.Model.FindEntityType(typeof(GelType));
        var formulation = gelType!.FindProperty(nameof(GelType.Formulation));

        Assert.NotNull(formulation);
        var converter = formulation!.GetValueConverter();
        Assert.NotNull(converter);
        Assert.Equal(typeof(Formulation), converter!.ModelClrType);
    }

    [Fact]
    public void WarehouseTask_origin_and_destination_map_via_cell_value_converters()
    {
        using var context = CreateContext();

        var task = context.Model.FindEntityType(typeof(WarehouseTask));

        var origin = task!.FindProperty(nameof(WarehouseTask.Origin));
        var destination = task.FindProperty(nameof(WarehouseTask.Destination));

        Assert.NotNull(origin);
        Assert.NotNull(destination);
        Assert.NotNull(origin!.GetValueConverter());
        Assert.NotNull(destination!.GetValueConverter());
    }

    [Fact]
    public void Starship_loading_windows_map_via_a_value_converter()
    {
        using var context = CreateContext();

        var starship = context.Model.FindEntityType(typeof(Starship));

        // Windows are persisted through the backing "_windows" field so the CLR type matches the
        // starship constructor parameter; the read-only Windows navigation is intentionally not mapped.
        var windows = starship!.FindProperty("_windows");

        Assert.NotNull(windows);
        Assert.NotNull(windows!.GetValueConverter());
    }

    [Fact]
    public void Worker_shifts_map_via_a_value_converter()
    {
        using var context = CreateContext();

        var worker = context.Model.FindEntityType(typeof(Worker));
        var shifts = worker!.FindProperty(nameof(Worker.Shifts));

        Assert.NotNull(shifts);
        Assert.NotNull(shifts!.GetValueConverter());
    }

    [Fact]
    public void ColonyOrder_lines_map_via_a_value_converter()
    {
        using var context = CreateContext();

        var order = context.Model.FindEntityType(typeof(ColonyOrder));
        var lines = order!.FindProperty(nameof(ColonyOrder.Lines));

        Assert.NotNull(lines);
        Assert.NotNull(lines!.GetValueConverter());
    }

    [Fact]
    public void Colony_demand_profile_maps_via_a_value_converter()
    {
        using var context = CreateContext();

        var colony = context.Model.FindEntityType(typeof(Colony));
        var profile = colony!.FindProperty(nameof(Colony.Profile));

        Assert.NotNull(profile);
        Assert.NotNull(profile!.GetValueConverter());
    }
}
