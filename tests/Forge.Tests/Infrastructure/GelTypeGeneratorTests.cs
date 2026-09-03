using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Infrastructure.Seeding;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="GelTypeGenerator"/> (task 29.1): the deterministic combinatorial
/// generator that seeds exactly 1000 distinct gel types, each with a unique id, a shelf-life
/// between 1 and 365 days, at least one flavor, and a storage temperature range (Req 25.1). Also
/// verifies determinism (identical seed reproduces the identical set) and seed sensitivity.
/// Validates: Requirements 25.1.
/// </summary>
public sealed class GelTypeGeneratorTests
{
    private const int Seed = 20240115;

    private static IReadOnlyList<GelType> Generate(int seed) =>
        new GelTypeGenerator().Generate(seed);

    [Fact]
    public void Produces_exactly_1000_gel_types()
    {
        var gelTypes = Generate(Seed);

        Assert.Equal(GelTypeGenerator.GelTypeCount, gelTypes.Count);
        Assert.Equal(1000, gelTypes.Count);
    }

    [Fact]
    public void All_ids_are_unique()
    {
        var gelTypes = Generate(Seed);

        var distinctIds = gelTypes.Select(g => g.Id).ToHashSet();

        Assert.Equal(gelTypes.Count, distinctIds.Count);
    }

    [Fact]
    public void All_formulations_are_distinct()
    {
        var gelTypes = Generate(Seed);

        var distinctFormulations = gelTypes.Select(g => g.Formulation).ToHashSet();

        Assert.Equal(gelTypes.Count, distinctFormulations.Count);
    }

    [Fact]
    public void Every_shelf_life_is_within_1_to_365_days()
    {
        var gelTypes = Generate(Seed);

        Assert.All(gelTypes, g =>
        {
            var days = g.Formulation.NominalShelfLife.TotalDays;
            Assert.True(days >= 1, $"Shelf-life {days} days is below the 1-day minimum.");
            Assert.True(days <= 365, $"Shelf-life {days} days exceeds the 365-day maximum.");
        });
    }

    [Fact]
    public void Every_gel_type_has_at_least_one_flavor()
    {
        var gelTypes = Generate(Seed);

        Assert.All(gelTypes, g =>
        {
            Assert.NotEmpty(g.Formulation.Flavors);
            Assert.All(g.Formulation.Flavors, f => Assert.False(string.IsNullOrWhiteSpace(f)));
        });
    }

    [Fact]
    public void Every_gel_type_has_a_valid_storage_range()
    {
        var gelTypes = Generate(Seed);

        Assert.All(gelTypes, g =>
        {
            var range = g.Formulation.StorageRange;
            Assert.True(
                range.MinCelsius <= range.MaxCelsius,
                $"Storage range [{range.MinCelsius}, {range.MaxCelsius}] is inverted.");
        });
    }

    [Fact]
    public void Every_velocity_is_finite_and_non_negative()
    {
        // The GelType constructor rejects negative/NaN/infinite velocities, so successful
        // construction already implies validity; assert explicitly for documentation.
        var gelTypes = Generate(Seed);

        Assert.All(gelTypes, g =>
        {
            Assert.False(double.IsNaN(g.Velocity));
            Assert.False(double.IsInfinity(g.Velocity));
            Assert.True(g.Velocity >= 0);
        });
    }

    [Fact]
    public void Identical_seed_reproduces_the_identical_set()
    {
        var first = Generate(Seed);
        var second = Generate(Seed);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            // Same ids, formulations, velocities, and order.
            Assert.Equal(first[i].Id, second[i].Id);
            Assert.Equal(first[i].Formulation, second[i].Formulation);
            Assert.Equal(first[i].Velocity, second[i].Velocity);
        }
    }

    [Fact]
    public void Different_seed_changes_the_set()
    {
        var first = Generate(Seed);
        var other = Generate(Seed + 1);

        // Ids are derived from the seed, so a different seed yields a disjoint id set.
        var firstIds = first.Select(g => g.Id).ToHashSet();
        var otherIds = other.Select(g => g.Id).ToHashSet();
        Assert.False(firstIds.SetEquals(otherIds));

        // The ordered sequence must also differ (order and/or membership changes with the seed).
        var sameOrder = first.Count == other.Count
            && first.Zip(other, (a, b) => a.Id == b.Id).All(x => x);
        Assert.False(sameOrder);
    }
}
