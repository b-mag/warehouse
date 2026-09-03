using System.Security.Cryptography;
using System.Text;
using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Infrastructure.Persistence;

namespace Forge.Infrastructure.Seeding;

/// <summary>
/// Deterministic warehouse seeder (Req 25.2–25.5). Given a <see cref="WarehouseSeedOptions"/> it builds a
/// realistic starting warehouse — the 1000 gel types (via <see cref="GelTypeGenerator"/>), 2–20 temperature
/// zones covering every gel type's storage range, 3–5 colonies each with a distinct demand profile, and
/// 1000–100000 gel lots placed so every zone has ≥1 lot and every lot sits in a compatible zone — and
/// persists the whole graph through the <see cref="ForgeDbContext"/> as a single unit of work.
/// <para>
/// <b>Determinism (Req 25 / design "Deterministic RNG").</b> Everything is a pure function of
/// <see cref="WarehouseSeedOptions.Seed"/>: gel types come from the seeded <see cref="GelTypeGenerator"/>,
/// and every zone / colony / lot id is derived from the seed plus a stable ordinal (never
/// <see cref="Guid.NewGuid"/>), mirroring the pattern <see cref="GelTypeGenerator"/> uses. An identical seed
/// therefore reproduces the identical warehouse (ids, counts, placements).
/// </para>
/// <para>
/// <b>Atomic abort (Req 25.5).</b> The seeder performs the full zone-compatibility check and builds the
/// entire object graph <em>in memory</em> before it touches the context. Only after every gel type is
/// confirmed to have a compatible zone does it add the aggregates and call
/// <see cref="ForgeDbContext.SaveChangesAsync(CancellationToken)"/> exactly once. If any gel type has no
/// compatible zone the method returns a <see cref="DomainError.Validation"/> failure naming the offending
/// gel type id and its storage requirement, having added nothing — so persistence is all-or-nothing even
/// on the EF Core in-memory provider (which has no real transactions). Where the underlying provider does
/// support transactions the single terminal <c>SaveChanges</c> is itself atomic.
/// </para>
/// <para>
/// <b>Testability seam.</b> Zones are normally derived from the same <see cref="GelTypeSeedTables.StorageBands"/>
/// the gel types are drawn from, so in normal seeding every gel type is contained by ≥1 zone <em>by
/// construction</em> and the abort path is unreachable. To keep Req 25.5 testable the seeder accepts two
/// optional seams: an injected gel-type source (<see cref="IGelTypeSource"/>) and an injected zone-band
/// source (<see cref="IZoneBandSource"/>). A test can supply a gel type whose storage range no injected
/// band contains to exercise the abort deterministically. Both default to the production sources.
/// </para>
/// </summary>
public sealed class WarehouseSeeder
{
    private readonly ForgeDbContext _context;
    private readonly IGelTypeSource _gelTypeSource;
    private readonly IZoneBandSource _zoneBandSource;

    /// <summary>
    /// Construct the seeder over the persistence context. Uses the production gel-type generator and the
    /// storage-class bands as the zone source, so normal seeding always produces full coverage.
    /// </summary>
    public WarehouseSeeder(ForgeDbContext context)
        : this(context, DefaultGelTypeSource.Instance, DefaultZoneBandSource.Instance)
    {
    }

    /// <summary>
    /// Construct the seeder with explicit sources. The <paramref name="gelTypeSource"/> and
    /// <paramref name="zoneBandSource"/> seams exist so the atomic-abort path (Req 25.5) can be exercised
    /// deterministically by supplying a gel type with no containing band. Production callers use the
    /// <see cref="WarehouseSeeder(ForgeDbContext)"/> overload.
    /// </summary>
    public WarehouseSeeder(
        ForgeDbContext context,
        IGelTypeSource gelTypeSource,
        IZoneBandSource zoneBandSource)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _gelTypeSource = gelTypeSource ?? throw new ArgumentNullException(nameof(gelTypeSource));
        _zoneBandSource = zoneBandSource ?? throw new ArgumentNullException(nameof(zoneBandSource));
    }

    /// <summary>
    /// Seed the warehouse deterministically and persist it atomically (Req 25.2–25.5).
    /// </summary>
    /// <param name="options">The deterministic seed inputs. Its ranges are validated first (Req 25.3, 25.4).</param>
    /// <param name="ct">Cancellation token propagated to the single terminal save.</param>
    /// <returns>
    /// A <see cref="WarehouseSeedReport"/> summarizing the persisted counts on success, or a
    /// <see cref="DomainError.Validation"/> failure (naming the offending gel type + storage requirement, or
    /// the offending option) on rejection — having persisted nothing.
    /// </returns>
    public async Task<Result<WarehouseSeedReport>> SeedAsync(
        WarehouseSeedOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // 1) Validate option ranges (Req 25.3, 25.4). Abort before any work if out of range.
        var validation = options.Validate();
        if (validation.IsFailure)
        {
            return Result<WarehouseSeedReport>.Failure(validation.Error);
        }

        // 2) Build the full graph in memory. NOTHING is added to the context until every check passes,
        //    which is what guarantees the atomic abort (Req 25.5) even under the in-memory provider.
        var gelTypes = _gelTypeSource.Generate(options.Seed);
        var zones = BuildZones(options.Seed);

        // 3) Compatibility check (Req 25.2 / abort path Req 25.5): every gel type's storage range must be
        //    contained by at least one zone. On the first violation, abort naming the gel type + requirement.
        var compatibleZoneByGelType = new Dictionary<GelTypeId, List<TemperatureZone>>();
        foreach (var gelType in gelTypes)
        {
            var required = gelType.Formulation.StorageRange;
            var matches = new List<TemperatureZone>();
            foreach (var zone in zones)
            {
                if (zone.AllowableRange.ContainsRange(required))
                {
                    matches.Add(zone);
                }
            }

            if (matches.Count == 0)
            {
                // Abort atomically: nothing has been added to the context yet (Req 25.5).
                return Result<WarehouseSeedReport>.Failure(DomainError.Validation(
                    $"Gel type {gelType.Id} has storage requirement " +
                    $"[{required.MinCelsius}..{required.MaxCelsius}]°C which is not contained within any " +
                    $"seeded temperature zone; seeding aborted without persisting (Req 25.2, 25.5).",
                    nameof(GelType)));
            }

            compatibleZoneByGelType[gelType.Id] = matches;
        }

        // 4) Colonies: 3..5, each with a demand profile distinct in ≥1 attribute (Req 25.3).
        var colonies = BuildColonies(options, gelTypes);

        // 5) Lots: 1000..100000 placed so every zone has ≥1 lot and every lot is in a compatible zone
        //    (Req 25.4). Built in memory; capacities are sized so every placement fits.
        var lots = BuildLots(options, gelTypes, zones, compatibleZoneByGelType);

        // 6) All checks passed. Persist the whole graph in one unit of work.
        _context.GelTypes.AddRange(gelTypes);
        _context.TemperatureZones.AddRange(zones);
        _context.Colonies.AddRange(colonies);
        _context.GelLots.AddRange(lots);

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result<WarehouseSeedReport>.Success(new WarehouseSeedReport(
            GelTypeCount: gelTypes.Count,
            ZoneCount: zones.Count,
            ColonyCount: colonies.Count,
            LotCount: lots.Count));
    }

    /// <summary>
    /// Derive one <see cref="TemperatureZone"/> per band supplied by the zone-band source (Req 25.2). In
    /// production the bands are the six <see cref="GelTypeSeedTables.StorageBands"/> the gel types are drawn
    /// from, so each gel type's range is contained by exactly the zone for its band — full coverage by
    /// construction. Zone count is therefore the band count, which sits inside the required 2..20 window.
    /// Each zone gets the maximum capacity so any lot distribution (up to <see cref="WarehouseSeedOptions.MaxLots"/>)
    /// fits regardless of how the lots concentrate.
    /// </summary>
    private List<TemperatureZone> BuildZones(int seed)
    {
        var bands = _zoneBandSource.Bands;
        var zones = new List<TemperatureZone>(bands.Count);

        for (var i = 0; i < bands.Count; i++)
        {
            var id = new ZoneId(DeriveGuid(seed, "zone", i));
            var result = TemperatureZone.Create(id, bands[i].Range, TemperatureZone.MaxCapacity);
            if (result.IsFailure)
            {
                // The bands are curated valid ranges and MaxCapacity is in range, so this is a
                // configuration fault (bad band table), not an expected runtime rejection.
                throw new InvalidOperationException(
                    $"Failed to construct seed zone for band '{bands[i].Name}': {result.Error}.");
            }

            zones.Add(result.Value);
        }

        return zones;
    }

    // Note: BuildZones consumes ZoneBand values (public TemperatureRange + name) so the seam types stay
    // public without leaking the internal StorageBand record.

    /// <summary>
    /// Build <see cref="WarehouseSeedOptions.ColonyCount"/> colonies (3..5), each with a demand profile that
    /// differs from every other in at least one attribute (Req 25.3). Distinctness is guaranteed by giving
    /// each colony a different base-rate scale and a different single trend boundary (different multiplier +
    /// start offset), so no two profiles are content-equal. Demand keys are a deterministic subset of the
    /// seeded gel type ids.
    /// </summary>
    private static List<Colony> BuildColonies(
        WarehouseSeedOptions options,
        IReadOnlyList<GelType> gelTypes)
    {
        // Use a small, stable subset of gel type ids as the demand keys (or all of them if there are fewer).
        var keyCount = Math.Min(8, gelTypes.Count);
        var demandKeys = new List<GelTypeId>(keyCount);
        for (var i = 0; i < keyCount; i++)
        {
            demandKeys.Add(gelTypes[i].Id);
        }

        var colonies = new List<Colony>(options.ColonyCount);
        var trendAnchor = options.ProducedAtOrDefault;

        for (var c = 0; c < options.ColonyCount; c++)
        {
            // Distinct base rates: each colony scales its rates differently so the base-rate maps differ.
            var baseScale = 1.0 + c; // 1.0, 2.0, 3.0, ... — all finite, non-negative.
            var rates = new Dictionary<GelTypeId, double>(demandKeys.Count);
            for (var k = 0; k < demandKeys.Count; k++)
            {
                rates[demandKeys[k]] = baseScale * (0.5 + (0.25 * k));
            }

            // Distinct trend: a single boundary whose start offset and multiplier vary per colony, so even
            // if two colonies ever shared base rates their trend lists would still differ.
            var trend = TrendBoundary.Create(
                trendAnchor.AddHours(c + 1),
                multiplier: 1.0 + (0.1 * (c + 1)));

            // TrendBoundary.Create only rejects NaN/∞/negative multipliers; ours are always valid.
            var trends = new List<TrendBoundary> { trend.Value };

            var profile = DemandProfile.Create(rates, trends);
            if (profile.IsFailure)
            {
                // Rates and trends are constructed valid; a failure would be a programming fault.
                throw new InvalidOperationException(
                    $"Failed to construct seed demand profile for colony {c}: {profile.Error}.");
            }

            var id = new ColonyId(DeriveGuid(options.Seed, "colony", c));
            colonies.Add(new Colony(id, profile.Value));
        }

        return colonies;
    }

    /// <summary>
    /// Build exactly <see cref="WarehouseSeedOptions.LotCount"/> lots (1000..100000) such that every zone
    /// receives at least one lot and every lot is placed in a zone compatible with its gel type (Req 25.4).
    /// <para>
    /// Placement strategy (deterministic): first, guarantee per-zone occupancy by placing one lot in each
    /// zone, choosing for each zone a gel type known to be compatible with it. Then fill the remaining lots
    /// by walking the gel types round-robin and placing each in its first compatible zone. Every lot derives
    /// its expiry from its gel type's formulation and is stamped with <see cref="WarehouseSeedOptions.ProducedAtOrDefault"/>.
    /// The zone's <see cref="TemperatureZone.StoredQuantity"/> is advanced (quantity 1 per lot) so it stays
    /// consistent with the lots actually placed in it; capacities are <see cref="TemperatureZone.MaxCapacity"/>
    /// so every placement fits.
    /// </para>
    /// </summary>
    private static List<GelLot> BuildLots(
        WarehouseSeedOptions options,
        IReadOnlyList<GelType> gelTypes,
        IReadOnlyList<TemperatureZone> zones,
        IReadOnlyDictionary<GelTypeId, List<TemperatureZone>> compatibleZoneByGelType)
    {
        var producedAt = options.ProducedAtOrDefault;
        var lots = new List<GelLot>(options.LotCount);

        // Index the first gel type compatible with each zone so we can guarantee per-zone occupancy.
        // Every zone is derived from a band, and (in production) at least the gel types drawn from that band
        // are compatible; we scan defensively so an injected zone with no matching gel type surfaces clearly.
        var firstGelTypeForZone = new Dictionary<ZoneId, GelType>();
        foreach (var gelType in gelTypes)
        {
            foreach (var zone in compatibleZoneByGelType[gelType.Id])
            {
                firstGelTypeForZone.TryAdd(zone.Id, gelType);
            }
        }

        var ordinal = 0;

        // Phase 1: one lot per zone (Req 25.4 per-zone occupancy). This consumes up to zones.Count lots; the
        // option floor of 1000 lots is far above the max 20 zones, so there is always room.
        foreach (var zone in zones)
        {
            if (!firstGelTypeForZone.TryGetValue(zone.Id, out var gelType))
            {
                // No gel type is compatible with this zone. This cannot happen in production (zones derive
                // from the same bands as the gel types); it would indicate an inconsistent injected source.
                throw new InvalidOperationException(
                    $"Seed zone {zone.Id} has no compatible gel type to guarantee occupancy (Req 25.4).");
            }

            lots.Add(MakeLot(options.Seed, ordinal++, gelType, producedAt, zone));
            Store(zone);
        }

        // Phase 2: fill the remaining lots, walking gel types round-robin and placing each in its first
        // compatible zone. Deterministic in (seed, ordinal).
        var gelIndex = 0;
        while (lots.Count < options.LotCount)
        {
            var gelType = gelTypes[gelIndex % gelTypes.Count];
            var zone = compatibleZoneByGelType[gelType.Id][0];

            lots.Add(MakeLot(options.Seed, ordinal++, gelType, producedAt, zone));
            Store(zone);

            gelIndex++;
        }

        return lots;
    }

    /// <summary>Advance a zone's stored quantity by one lot, keeping it consistent with placed lots.</summary>
    private static void Store(TemperatureZone zone)
    {
        var stored = zone.TryStore(1);
        if (stored.IsFailure)
        {
            // Capacities are MaxCapacity and the lot count never exceeds MaxLots (== MaxCapacity), so a
            // single zone can never overflow here; a failure would be a programming fault.
            throw new InvalidOperationException(
                $"Seed zone {zone.Id} overflowed while placing seed lots: {stored.Error}.");
        }
    }

    /// <summary>Create one deterministic lot (quantity 1) assigned to a compatible zone (Req 25.4).</summary>
    private static GelLot MakeLot(
        int seed,
        int ordinal,
        GelType gelType,
        DateTimeOffset producedAt,
        TemperatureZone zone)
    {
        var id = new GelLotId(DeriveGuid(seed, "lot", ordinal));
        return GelLot.Create(
            id,
            gelType,
            producedAt,
            quantity: 1,
            fefoPriority: 0,
            assignedZoneId: zone.Id);
    }

    /// <summary>
    /// Derive a stable, deterministic <see cref="Guid"/> from the seed, a namespace tag, and an ordinal —
    /// the same MD5-of-bytes technique <see cref="GelTypeGenerator"/> uses for its ids (no security implied;
    /// it is purely a reproducible 128-bit hash so seeding never touches <see cref="Guid.NewGuid"/>).
    /// </summary>
    private static Guid DeriveGuid(int seed, string tag, int ordinal)
    {
        var payload = Encoding.UTF8.GetBytes($"forge-{tag}::{seed}::{ordinal}");
        var digest = MD5.HashData(payload); // 16 bytes -> exactly one Guid.
        return new Guid(digest);
    }
}

/// <summary>
/// Seam for the seeder's gel-type set (Req 25.5 testability). Production uses
/// <see cref="DefaultGelTypeSource"/> which delegates to <see cref="GelTypeGenerator"/>; tests may supply a
/// source containing a gel type with no containing zone band to exercise the atomic-abort path.
/// </summary>
public interface IGelTypeSource
{
    /// <summary>Produce the gel types to seed for the given <paramref name="seed"/>.</summary>
    IReadOnlyList<GelType> Generate(int seed);
}

/// <summary>Production gel-type source: exactly 1000 distinct gel types via <see cref="GelTypeGenerator"/>.</summary>
public sealed class DefaultGelTypeSource : IGelTypeSource
{
    /// <summary>Shared singleton; <see cref="GelTypeGenerator"/> is stateless.</summary>
    public static readonly DefaultGelTypeSource Instance = new();

    private readonly GelTypeGenerator _generator = new();

    /// <inheritdoc />
    public IReadOnlyList<GelType> Generate(int seed) => _generator.Generate(seed);
}

/// <summary>
/// A named temperature-zone band the seeder derives a zone from: a public analogue of the internal
/// <c>StorageBand</c> so the <see cref="IZoneBandSource"/> seam can live in the public surface without
/// leaking the internal seed tables.
/// </summary>
/// <param name="Name">Human-readable band label used only in diagnostics.</param>
/// <param name="Range">The inclusive allowable temperature range for the derived zone (Req 25.2).</param>
public readonly record struct ZoneBand(string Name, TemperatureRange Range);

/// <summary>
/// Seam for the temperature-zone bands the seeder derives zones from (Req 25.2 / 25.5 testability).
/// Production uses <see cref="DefaultZoneBandSource"/> (the same storage-class bands the gel types are
/// drawn from, guaranteeing coverage by construction); tests may narrow the bands so a supplied gel type
/// falls outside all of them and the abort path fires.
/// </summary>
public interface IZoneBandSource
{
    /// <summary>The bands to derive zones from; must yield 2..20 zones (Req 25.2).</summary>
    IReadOnlyList<ZoneBand> Bands { get; }
}

/// <summary>Production zone-band source: the six curated storage-class bands (Req 25.2).</summary>
public sealed class DefaultZoneBandSource : IZoneBandSource
{
    /// <summary>Shared singleton over the band table.</summary>
    public static readonly DefaultZoneBandSource Instance = new();

    private readonly IReadOnlyList<ZoneBand> _bands = BuildBands();

    /// <inheritdoc />
    public IReadOnlyList<ZoneBand> Bands => _bands;

    // Project the internal storage-class band table into the public ZoneBand shape. Kept in sync with the
    // gel-type generator's bands so every seeded gel type is contained by exactly one zone (Req 25.2).
    private static IReadOnlyList<ZoneBand> BuildBands()
    {
        var source = GelTypeSeedTables.StorageBands;
        var bands = new List<ZoneBand>(source.Count);
        foreach (var band in source)
        {
            bands.Add(new ZoneBand(band.Name, band.Range));
        }

        return bands;
    }
}
