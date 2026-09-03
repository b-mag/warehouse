using Forge.Domain.Common;

namespace Forge.Infrastructure.Seeding;

/// <summary>
/// Deterministic inputs to <see cref="WarehouseSeeder"/> (Req 25). Everything the seed produces is a
/// pure function of these options, so an identical <see cref="Seed"/> reproduces an identical warehouse
/// (same gel types, zones, colonies, and lots). Defaults keep every produced count comfortably inside the
/// ranges the requirements mandate.
/// </summary>
/// <param name="Seed">
/// The PRNG seed threaded through <see cref="GelTypeGenerator"/> and the seeder's own streams. Identical
/// seeds reproduce the identical warehouse; different seeds vary it.
/// </param>
/// <param name="ColonyCount">
/// How many colonies to seed; must be within 3..5 (Req 25.3). Each gets a demand profile differing from
/// every other in at least one attribute.
/// </param>
/// <param name="LotCount">
/// How many gel lots to seed; must be within 1000..100000 (Req 25.4). Every zone receives at least one
/// lot and every lot is placed in a zone compatible with its gel type.
/// </param>
/// <param name="ProducedAt">
/// The production timestamp stamped on every seeded lot; each lot derives its own expiry from its gel
/// type's nominal shelf-life (Req 3.4, 11.4). Fixed here so seeding stays deterministic.
/// </param>
public sealed record WarehouseSeedOptions(
    int Seed = 0,
    int ColonyCount = 4,
    int LotCount = 1000,
    DateTimeOffset? ProducedAt = null)
{
    /// <summary>Smallest permitted colony count (Req 25.3).</summary>
    public const int MinColonies = 3;

    /// <summary>Largest permitted colony count (Req 25.3).</summary>
    public const int MaxColonies = 5;

    /// <summary>Smallest permitted lot count (Req 25.4).</summary>
    public const int MinLots = 1000;

    /// <summary>Largest permitted lot count (Req 25.4).</summary>
    public const int MaxLots = 100_000;

    /// <summary>The production timestamp to stamp on lots, defaulting to a fixed deterministic anchor.</summary>
    public DateTimeOffset ProducedAtOrDefault =>
        ProducedAt ?? new DateTimeOffset(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Validate the option ranges (Req 25.3, 25.4). Returns a <see cref="DomainError.Validation"/> naming
    /// the offending option on failure, leaving the seeder to abort before any work is done.
    /// </summary>
    public Result Validate()
    {
        if (ColonyCount is < MinColonies or > MaxColonies)
        {
            return DomainError.Validation(
                $"Colony count must be between {MinColonies} and {MaxColonies}; got {ColonyCount}.",
                nameof(ColonyCount));
        }

        if (LotCount is < MinLots or > MaxLots)
        {
            return DomainError.Validation(
                $"Lot count must be between {MinLots} and {MaxLots}; got {LotCount}.",
                nameof(LotCount));
        }

        return Result.Success();
    }
}

/// <summary>
/// A summary of what a successful <see cref="WarehouseSeeder.SeedAsync"/> persisted (Req 25.1–25.4).
/// Returned inside a <see cref="Result{T}"/> so callers (and the seeding integration test, task 29.3)
/// can assert the produced counts fall within the required ranges.
/// </summary>
/// <param name="GelTypeCount">Number of distinct gel types persisted (exactly 1000, Req 25.1).</param>
/// <param name="ZoneCount">Number of temperature zones persisted (2..20, Req 25.2).</param>
/// <param name="ColonyCount">Number of colonies persisted (3..5, Req 25.3).</param>
/// <param name="LotCount">Number of gel lots persisted (1000..100000, Req 25.4).</param>
public sealed record WarehouseSeedReport(
    int GelTypeCount,
    int ZoneCount,
    int ColonyCount,
    int LotCount);
