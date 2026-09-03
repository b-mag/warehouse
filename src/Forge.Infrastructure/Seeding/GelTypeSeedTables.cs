using Forge.Domain.ColdChain;

namespace Forge.Infrastructure.Seeding;

/// <summary>
/// Curated descriptor tables that feed the combinatorial assembly performed by
/// <see cref="GelTypeGenerator"/> (Req 25.1, design "Seeding"). Kept BCL-only and side-effect
/// free so the generation is a pure function of these tables plus a seed.
/// <para>
/// The design calls for realistic diversity produced by
/// <c>flavor descriptors × storage-class bands × shelf-life buckets × velocity tiers</c>, sampled
/// without collision to yield 1000 distinct formulations. These tables are sized so the full
/// Cartesian product comfortably exceeds 1000 combinations, leaving ample room to sample exactly
/// 1000 unique gel-type formulations.
/// </para>
/// </summary>
internal static class GelTypeSeedTables
{
    /// <summary>
    /// The storage-class temperature bands (frozen / chilled / cool / ambient), each expressed as
    /// an inclusive Celsius <see cref="TemperatureRange"/>. Zones seeded later (task 29.2) are
    /// derived from these same bands so every gel type is guaranteed a compatible zone (Req 25.2).
    /// </summary>
    public static readonly IReadOnlyList<StorageBand> StorageBands =
    [
        new StorageBand("Frozen", new TemperatureRange(-25m, -18m)),
        new StorageBand("DeepChilled", new TemperatureRange(-5m, 0m)),
        new StorageBand("Chilled", new TemperatureRange(0m, 4m)),
        new StorageBand("Cool", new TemperatureRange(4m, 8m)),
        new StorageBand("Cellar", new TemperatureRange(8m, 12m)),
        new StorageBand("Ambient", new TemperatureRange(15m, 22m)),
    ];

    /// <summary>
    /// Shelf-life buckets, all strictly within the required 1..365 day window (Req 25.1). Chosen as
    /// realistic cold-chain durations from a few days up to a full year.
    /// </summary>
    public static readonly IReadOnlyList<int> ShelfLifeDayBuckets =
    [
        3, 7, 14, 21, 30, 45, 60, 90, 120, 180, 270, 365,
    ];

    /// <summary>
    /// Velocity (turnover-rate) tiers used by later velocity-affinity slotting (Req 16). All are
    /// finite and non-negative as <see cref="Forge.Domain.Gels.GelType"/> requires.
    /// </summary>
    public static readonly IReadOnlyList<double> VelocityTiers =
    [
        0.5, 1.0, 2.5, 5.0, 10.0, 25.0,
    ];

    /// <summary>
    /// Descriptive prefixes combined with <see cref="FlavorBases"/> to form flavor attribute
    /// strings (e.g. "Glacial Kelp-Protein"). Every seeded gel type carries at least one flavor
    /// (Req 25.1).
    /// </summary>
    public static readonly IReadOnlyList<string> FlavorPrefixes =
    [
        "Glacial", "Solar", "Nebular", "Verdant", "Umami", "Smoked",
        "Spiced", "Zesty", "Mellow", "Bright", "Crystalline", "Fermented",
    ];

    /// <summary>Base flavor bodies combined with <see cref="FlavorPrefixes"/>.</summary>
    public static readonly IReadOnlyList<string> FlavorBases =
    [
        "Kelp-Protein", "Algae-Bloom", "Citrus-Curd", "Cocoa-Mineral",
        "Berry-Enzyme", "Root-Starch", "Mushroom-Broth", "Honey-Pollen",
        "Chili-Oil", "Vanilla-Cream", "Sea-Salt", "Green-Tea",
    ];
}

/// <summary>A named storage-class band paired with its inclusive Celsius temperature range.</summary>
/// <param name="Name">Human-readable band label used for descriptive diagnostics.</param>
/// <param name="Range">The inclusive storage temperature range for the band (Req 25.1).</param>
internal readonly record struct StorageBand(string Name, TemperatureRange Range);
