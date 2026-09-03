using System.Security.Cryptography;
using System.Text;
using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Infrastructure.Seeding;

/// <summary>
/// Deterministic combinatorial generator that produces exactly 1000 distinct
/// <see cref="GelType"/> formulations for warehouse seeding (Req 25.1, design "Seeding").
/// <para>
/// Diversity is produced by combinatorial assembly of the curated
/// <see cref="GelTypeSeedTables"/>: <c>flavor descriptors × storage-class bands × shelf-life
/// buckets × velocity tiers</c>. The full candidate space is enumerated in a fixed order, shuffled
/// with a seeded pseudo-random number generator, and then walked — skipping any candidate whose
/// <see cref="Formulation"/> collides (by value equality) with one already produced — until exactly
/// 1000 unique formulations have been collected.
/// </para>
/// <para>
/// <b>Determinism.</b> Everything is a pure function of the <c>seed</c>: the shuffle draws from a
/// seeded <see cref="Random"/>, and each <see cref="GelTypeId"/> is derived deterministically from
/// the seed plus the candidate's stable ordinal (never <see cref="Guid.NewGuid"/>). Consequently an
/// identical seed always yields the identical set of 1000 gel types — same ids, formulations,
/// velocities, and order — while a different seed yields a different set.
/// </para>
/// </summary>
public sealed class GelTypeGenerator
{
    /// <summary>The exact number of gel types the seeder requires (Req 25.1).</summary>
    public const int GelTypeCount = 1000;

    private const int MinShelfLifeDays = 1;
    private const int MaxShelfLifeDays = 365;

    /// <summary>
    /// Generate the deterministic set of exactly <see cref="GelTypeCount"/> distinct gel types for
    /// the given <paramref name="seed"/>.
    /// </summary>
    /// <param name="seed">
    /// The PRNG seed. Identical seeds reproduce the identical set; different seeds change it.
    /// </param>
    /// <returns>
    /// A read-only list of exactly 1000 gel types, each with a unique id, a shelf-life between 1 and
    /// 365 days, at least one flavor, and a storage temperature range. The order is deterministic
    /// for a given seed.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The curated descriptor tables cannot yield 1000 distinct formulations. This is a
    /// configuration error in <see cref="GelTypeSeedTables"/>, not a runtime input error.
    /// </exception>
    public IReadOnlyList<GelType> Generate(int seed)
    {
        // 1) Enumerate the full candidate space in a fixed, stable order. Each candidate carries a
        //    stable ordinal so a deterministic id can be derived regardless of later shuffling.
        var candidates = EnumerateCandidates();

        // 2) Shuffle with a seeded PRNG (Fisher–Yates) so the sampled 1000 vary by seed while
        //    remaining fully reproducible for a given seed.
        var random = new Random(seed);
        ShuffleInPlace(candidates, random);

        // 3) Walk the shuffled candidates, collecting distinct-by-value formulations until we have
        //    exactly 1000. Formulation has value equality (including flavors), so a HashSet dedups.
        var seenFormulations = new HashSet<Formulation>();
        var result = new List<GelType>(GelTypeCount);

        foreach (var candidate in candidates)
        {
            var formulation = candidate.ToFormulation();
            if (!seenFormulations.Add(formulation))
            {
                continue; // Collision by value — skip so the produced set stays unique.
            }

            var id = DeriveId(seed, candidate.Ordinal);
            result.Add(new GelType(id, formulation, candidate.Velocity));

            if (result.Count == GelTypeCount)
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            $"The gel-type descriptor tables produced only {result.Count} distinct formulations; " +
            $"{GelTypeCount} are required (Req 25.1). Expand GelTypeSeedTables.");
    }

    /// <summary>
    /// Enumerate the Cartesian product of the descriptor tables in a fixed order. A secondary
    /// flavor is folded in on a stable schedule to widen the distinct-formulation space while
    /// keeping every candidate's (band, shelf-life, flavors) triple stable and reproducible.
    /// </summary>
    private static List<Candidate> EnumerateCandidates()
    {
        var bands = GelTypeSeedTables.StorageBands;
        var shelfBuckets = GelTypeSeedTables.ShelfLifeDayBuckets;
        var velocities = GelTypeSeedTables.VelocityTiers;
        var prefixes = GelTypeSeedTables.FlavorPrefixes;
        var bases = GelTypeSeedTables.FlavorBases;

        var candidates = new List<Candidate>(
            bands.Count * shelfBuckets.Count * velocities.Count * prefixes.Count * bases.Count);

        var ordinal = 0;
        for (var b = 0; b < bands.Count; b++)
        {
            for (var s = 0; s < shelfBuckets.Count; s++)
            {
                var days = shelfBuckets[s];

                // Shelf-life buckets are curated inside 1..365; assert the invariant defensively so
                // a bad edit to the table surfaces here rather than as a silently invalid gel type.
                if (days < MinShelfLifeDays || days > MaxShelfLifeDays)
                {
                    throw new InvalidOperationException(
                        $"Shelf-life bucket {days} is outside the required 1..365 day window (Req 25.1).");
                }

                for (var v = 0; v < velocities.Count; v++)
                {
                    for (var p = 0; p < prefixes.Count; p++)
                    {
                        for (var f = 0; f < bases.Count; f++)
                        {
                            var primary = $"{prefixes[p]} {bases[f]}";

                            // Add a distinct second flavor on an even schedule for extra variety.
                            // Every candidate still has >= 1 flavor (Req 25.1).
                            string[] flavors;
                            if ((ordinal & 1) == 0)
                            {
                                flavors = [primary];
                            }
                            else
                            {
                                var secondary =
                                    $"{prefixes[(p + 1) % prefixes.Count]} {bases[(f + 3) % bases.Count]}";
                                flavors = string.Equals(secondary, primary, StringComparison.Ordinal)
                                    ? [primary]
                                    : [primary, secondary];
                            }

                            candidates.Add(new Candidate(
                                Ordinal: ordinal,
                                Band: bands[b],
                                ShelfLifeDays: days,
                                Velocity: velocities[v],
                                Flavors: flavors));

                            ordinal++;
                        }
                    }
                }
            }
        }

        return candidates;
    }

    /// <summary>Deterministic in-place Fisher–Yates shuffle driven by the seeded PRNG.</summary>
    private static void ShuffleInPlace(List<Candidate> items, Random random)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Derive a stable, deterministic <see cref="GelTypeId"/> from the seed and the candidate's
    /// ordinal. Uses an MD5 digest of the (seed, ordinal) bytes purely as a fixed 128-bit hash to
    /// fill a <see cref="Guid"/> — no security is implied; it just gives a reproducible id that does
    /// not depend on <see cref="Guid.NewGuid"/>.
    /// </summary>
    private static GelTypeId DeriveId(int seed, int ordinal)
    {
        var payload = Encoding.UTF8.GetBytes($"forge-gel-type::{seed}::{ordinal}");
        var digest = MD5.HashData(payload); // 16 bytes -> exactly one Guid.
        return new GelTypeId(new Guid(digest));
    }

    /// <summary>
    /// One point in the candidate space. <see cref="Ordinal"/> is the stable pre-shuffle index used
    /// only to derive a reproducible id; it is not part of formulation equality.
    /// </summary>
    private readonly record struct Candidate(
        int Ordinal,
        StorageBand Band,
        int ShelfLifeDays,
        double Velocity,
        IReadOnlyList<string> Flavors)
    {
        public Formulation ToFormulation() =>
            new(Band.Range, TimeSpan.FromDays(ShelfLifeDays), Flavors);
    }
}
