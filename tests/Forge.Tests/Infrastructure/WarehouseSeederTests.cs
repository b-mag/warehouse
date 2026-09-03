using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Infrastructure.Persistence;
using Forge.Infrastructure.Seeding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Integration tests for <see cref="WarehouseSeeder"/> (task 29.2, Req 25.2–25.5). They run the seeder over
/// a fresh EF Core in-memory database and assert the produced counts, coverage, colony distinctness, per-zone
/// occupancy, determinism, and — crucially — that an incompatible gel type aborts the whole seed without
/// persisting anything (atomic abort, Req 25.5).
/// <para>
/// The abort path is unreachable in normal seeding (production zones derive from the same storage-class bands
/// the gel types are drawn from, so coverage is guaranteed by construction). It is exercised deterministically
/// through the seeder's <see cref="IGelTypeSource"/> seam: a source that includes a gel type whose storage
/// range no zone band contains forces the compatibility check to fail.
/// </para>
/// </summary>
public sealed class WarehouseSeederTests
{
    private static ForgeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ForgeDbContext>()
            .UseInMemoryDatabase($"forge-seed-{Guid.NewGuid()}")
            .Options);

    // Keep the lot count at the minimum so the tests stay fast while still exercising every requirement.
    private static WarehouseSeedOptions Options(int seed = 1234, int colonyCount = 4, int lotCount = 1000) =>
        new(Seed: seed, ColonyCount: colonyCount, LotCount: lotCount);

    [Fact]
    public async Task Seeds_exactly_1000_gel_types()
    {
        await using var ctx = NewContext();
        var seeder = new WarehouseSeeder(ctx);

        var result = await seeder.SeedAsync(Options());

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Value.GelTypeCount);
        Assert.Equal(1000, await ctx.GelTypes.CountAsync());
    }

    [Fact]
    public async Task Seeds_between_2_and_20_zones_with_full_storage_coverage()
    {
        await using var ctx = NewContext();
        var seeder = new WarehouseSeeder(ctx);

        var result = await seeder.SeedAsync(Options());

        Assert.True(result.IsSuccess);
        Assert.InRange(result.Value.ZoneCount, 2, 20);

        var zones = await ctx.TemperatureZones.ToListAsync();
        var gelTypes = await ctx.GelTypes.ToListAsync();

        // Req 25.2: every gel type's storage range is contained by at least one zone's allowable range.
        Assert.All(gelTypes, gt =>
            Assert.Contains(zones, z => z.AllowableRange.ContainsRange(gt.Formulation.StorageRange)));
    }

    [Fact]
    public async Task Seeds_between_3_and_5_colonies_each_distinct_in_at_least_one_attribute()
    {
        await using var ctx = NewContext();
        var seeder = new WarehouseSeeder(ctx);

        var result = await seeder.SeedAsync(Options(colonyCount: 5));

        Assert.True(result.IsSuccess);
        Assert.InRange(result.Value.ColonyCount, 3, 5);

        var colonies = await ctx.Colonies.ToListAsync();
        Assert.Equal(5, colonies.Count);

        // Req 25.3: each colony's profile differs from every other in >= 1 attribute. DemandProfile uses
        // content equality, so pairwise inequality of the profiles proves distinctness.
        for (var i = 0; i < colonies.Count; i++)
        {
            for (var j = i + 1; j < colonies.Count; j++)
            {
                Assert.NotEqual(colonies[i].Profile, colonies[j].Profile);
            }
        }
    }

    [Fact]
    public async Task Seeds_requested_lot_count_with_every_zone_occupied_and_every_lot_in_a_compatible_zone()
    {
        await using var ctx = NewContext();
        var seeder = new WarehouseSeeder(ctx);

        var result = await seeder.SeedAsync(Options(lotCount: 1000));

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Value.LotCount);

        var lots = await ctx.GelLots.ToListAsync();
        var zones = await ctx.TemperatureZones.ToListAsync();
        var gelTypesById = (await ctx.GelTypes.ToListAsync()).ToDictionary(gt => gt.Id);
        var zonesById = zones.ToDictionary(z => z.Id);

        Assert.Equal(1000, lots.Count);

        // Req 25.4: every zone has >= 1 lot.
        var occupiedZoneIds = lots
            .Where(l => l.AssignedZoneId is not null)
            .Select(l => l.AssignedZoneId!.Value)
            .ToHashSet();
        Assert.All(zones, z => Assert.Contains(z.Id, occupiedZoneIds));

        // Req 25.4: every lot sits in a zone compatible with its gel type's storage requirement.
        Assert.All(lots, lot =>
        {
            Assert.NotNull(lot.AssignedZoneId);
            var zone = zonesById[lot.AssignedZoneId!.Value];
            var required = gelTypesById[lot.GelTypeId].Formulation.StorageRange;
            Assert.True(zone.AllowableRange.ContainsRange(required));
        });
    }

    [Fact]
    public async Task Identical_seed_reproduces_the_identical_warehouse()
    {
        var options = Options(seed: 4242);

        await using var ctxA = NewContext();
        await using var ctxB = NewContext();

        var resultA = await new WarehouseSeeder(ctxA).SeedAsync(options);
        var resultB = await new WarehouseSeeder(ctxB).SeedAsync(options);

        Assert.True(resultA.IsSuccess);
        Assert.True(resultB.IsSuccess);

        // Reports match.
        Assert.Equal(resultA.Value, resultB.Value);

        // Graph ids match: gel types, zones, colonies, and lots (with their placements).
        var gelTypesA = (await ctxA.GelTypes.ToListAsync()).Select(g => g.Id).OrderBy(id => id).ToList();
        var gelTypesB = (await ctxB.GelTypes.ToListAsync()).Select(g => g.Id).OrderBy(id => id).ToList();
        Assert.Equal(gelTypesA, gelTypesB);

        var zonesA = (await ctxA.TemperatureZones.ToListAsync()).Select(z => z.Id).OrderBy(id => id).ToList();
        var zonesB = (await ctxB.TemperatureZones.ToListAsync()).Select(z => z.Id).OrderBy(id => id).ToList();
        Assert.Equal(zonesA, zonesB);

        var coloniesA = (await ctxA.Colonies.ToListAsync()).Select(c => c.Id).OrderBy(id => id).ToList();
        var coloniesB = (await ctxB.Colonies.ToListAsync()).Select(c => c.Id).OrderBy(id => id).ToList();
        Assert.Equal(coloniesA, coloniesB);

        var lotsA = (await ctxA.GelLots.ToListAsync())
            .Select(l => (l.Id, l.GelTypeId, l.AssignedZoneId))
            .OrderBy(t => t.Id)
            .ToList();
        var lotsB = (await ctxB.GelLots.ToListAsync())
            .Select(l => (l.Id, l.GelTypeId, l.AssignedZoneId))
            .OrderBy(t => t.Id)
            .ToList();
        Assert.Equal(lotsA, lotsB);
    }

    [Fact]
    public async Task Aborts_without_persisting_and_names_gel_type_and_requirement_when_a_gel_type_has_no_compatible_zone()
    {
        await using var ctx = NewContext();

        // Seam: a gel-type source whose set contains one gel type with a storage range no production zone
        // band contains (200..300 °C is well outside every seeded band), forcing the abort path (Req 25.5).
        var incompatible = new GelType(
            new GelTypeId(new Guid("11111111-1111-1111-1111-111111111111")),
            new Formulation(new TemperatureRange(200m, 300m), TimeSpan.FromDays(30), new[] { "Molten Lava-Curd" }),
            velocity: 1.0);

        var seeder = new WarehouseSeeder(
            ctx,
            new IncompatibleGelTypeSource(incompatible),
            DefaultZoneBandSource.Instance);

        var result = await seeder.SeedAsync(Options());

        // Returns a failure naming the offending gel type + its storage requirement.
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains(incompatible.Id.ToString(), result.Error.Message);
        Assert.Contains("200", result.Error.Message);
        Assert.Contains("300", result.Error.Message);

        // Req 25.5: nothing persisted — every DbSet is empty.
        Assert.Equal(0, await ctx.GelTypes.CountAsync());
        Assert.Equal(0, await ctx.TemperatureZones.CountAsync());
        Assert.Equal(0, await ctx.Colonies.CountAsync());
        Assert.Equal(0, await ctx.GelLots.CountAsync());
    }

    [Fact]
    public async Task Rejects_option_ranges_outside_the_required_bounds()
    {
        await using var ctx = NewContext();
        var seeder = new WarehouseSeeder(ctx);

        // Colony count below the 3..5 window (Req 25.3).
        var result = await seeder.SeedAsync(new WarehouseSeedOptions(ColonyCount: 2));

        Assert.True(result.IsFailure);
        Assert.Equal(0, await ctx.GelTypes.CountAsync());
    }

    /// <summary>
    /// Test seam: the production 1000 gel types plus one deliberately-incompatible gel type, used to drive
    /// the atomic-abort path (Req 25.5) deterministically.
    /// </summary>
    private sealed class IncompatibleGelTypeSource(GelType incompatible) : IGelTypeSource
    {
        public IReadOnlyList<GelType> Generate(int seed)
        {
            var produced = new List<GelType>(new GelTypeGenerator().Generate(seed)) { incompatible };
            return produced;
        }
    }
}
