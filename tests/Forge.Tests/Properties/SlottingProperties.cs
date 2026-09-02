using CsCheck;
using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Slotting;
using Forge.Application.Slotting;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 9: Slotting determinism and compatibility
//
// Validates: Requirements 16.1, 16.2, 16.5, 28.7
//
// For any inventory state (zone set + occupancy) and any slotting input (gel type / lot), BOTH
// Phase-1 strategies — VelocityAffinitySlottingStrategy (default) and NaiveFirstAvailableStrategy —
// SHALL:
//   (a) select only a compatible zone (its allowable range contains the gel type's storage range)
//       that has available capacity (Req 16.1);
//   (b) select an identical zone for two selections over identical state + inputs (Req 16.5);
//   (c) break ties by ascending zone identifier (Req 16.2);
//   (d) report the lot as unslottable when no compatible zone with capacity exists (Req 16.3).
public sealed class SlottingProperties
{
    private const int Iterations = 100;

    private static readonly DateTimeOffset ProducedAt = new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A minimal in-test occupancy view kept consistent with the zone snapshots: a zone's remaining
    /// capacity is <c>Capacity − StoredQuantity</c> and its effective occupancy is exactly the stored
    /// quantity. Unknown zones report zero occupancy / zero remaining capacity so an out-of-set zone id
    /// could never be selected as "having capacity".
    /// </summary>
    private sealed class MapZoneOccupancy : IZoneOccupancy
    {
        private readonly Dictionary<ZoneId, TemperatureZone> _zones;

        public MapZoneOccupancy(IEnumerable<TemperatureZone> zones) =>
            _zones = zones.ToDictionary(z => z.Id);

        public int RemainingCapacity(ZoneId zone) =>
            _zones.TryGetValue(zone, out var z) ? z.RemainingCapacity : 0;

        public int Occupancy(ZoneId zone) =>
            _zones.TryGetValue(zone, out var z) ? z.StoredQuantity : 0;
    }

    /// <summary>
    /// A blueprint for a zone: raw generated fields kept separate from the constructed
    /// <see cref="TemperatureZone"/> so the generator stays simple and the zone is built via the
    /// validated domain factory.
    /// </summary>
    private readonly record struct ZoneSpec(
        Guid Id,
        decimal Min,
        decimal Max,
        int Capacity,
        int Stored);

    // Distinct GUIDs so the ascending-zone-id tie-break is fully determined and exercised.
    private static readonly Gen<Guid> GenGuid =
        Gen.Int[0, 100_000].Select(i =>
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(i).CopyTo(bytes, 0);
            return new Guid(bytes);
        });

    // Zone allowable bands are drawn from a small integer temperature grid so that many zones share
    // identical bands — that is what forces the tie-break (multiple equally-preferred candidates) and
    // makes "contains the storage range" reachable for a meaningful fraction of inputs.
    private static readonly Gen<(int, int)> GenBand =
        from a in Gen.Int[-20, 20]
        from b in Gen.Int[-20, 20]
        select (Math.Min(a, b), Math.Max(a, b));

    private static readonly Gen<ZoneSpec> GenZoneSpec =
        from id in GenGuid
        from band in GenBand
        from capacity in Gen.Int[1, 20]
        from stored in Gen.Int[0, 25] // may exceed capacity; clamped on build so some zones are full
        select new ZoneSpec(id, band.Item1, band.Item2, capacity, Math.Min(stored, capacity));

    private static readonly Gen<List<ZoneSpec>> GenZones =
        GenZoneSpec.List[0, 12].Select(list =>
            list.GroupBy(z => z.Id).Select(g => g.First()).ToList());

    // The gel's required storage band, drawn from the same grid so a zone band can contain it.
    private static readonly Gen<(int, int)> GenStorage =
        from a in Gen.Int[-15, 15]
        from b in Gen.Int[-15, 15]
        select (Math.Min(a, b), Math.Max(a, b));

    // Velocity spans zero (indifferent fast/slow) through fast movers so the velocity weighting and
    // the velocity-0 tie-break path are both exercised.
    private static readonly Gen<double> GenVelocity =
        Gen.Double[0.0, 50.0];

    private static readonly ISlottingStrategy[] Strategies =
    {
        new VelocityAffinitySlottingStrategy(),
        new NaiveFirstAvailableStrategy(),
    };

    private static TemperatureZone BuildZone(ZoneSpec spec)
    {
        var created = TemperatureZone.Create(
            new ZoneId(spec.Id),
            new TemperatureRange(spec.Min, spec.Max),
            spec.Capacity,
            spec.Stored);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static (GelType GelType, GelLot Lot) BuildGel(int storageMin, int storageMax, double velocity)
    {
        var formulation = new Formulation(
            new TemperatureRange(storageMin, storageMax),
            TimeSpan.FromDays(30),
            new[] { "vanilla" });
        var gelType = new GelType(new GelTypeId(Guid.NewGuid()), formulation, velocity);
        var lot = GelLot.Create(new GelLotId(Guid.NewGuid()), gelType, ProducedAt, quantity: 1);
        return (gelType, lot);
    }

    [Fact]
    public void Slotting_Compatibility_Determinism_TieBreak_And_Unslottable()
    {
        Gen.Select(GenZones, GenStorage, GenVelocity)
            .Sample((zoneSpecs, storage, velocity) =>
            {
                var zones = zoneSpecs.Select(BuildZone).ToList();
                var (gelType, lot) = BuildGel(storage.Item1, storage.Item2, velocity);
                var occupancy = new MapZoneOccupancy(zones);
                var storageRange = new TemperatureRange(storage.Item1, storage.Item2);

                // Ground-truth compatible-with-capacity set: zone allowable range contains the gel
                // storage range AND remaining capacity > 0 — ordered ascending by zone id.
                var compatibleOrdered = zones
                    .Where(z => z.AllowableRange.ContainsRange(storageRange) && z.RemainingCapacity > 0)
                    .OrderBy(z => z.Id)
                    .ToList();
                var compatibleIds = compatibleOrdered.Select(z => z.Id).ToHashSet();
                var hasCompatible = compatibleOrdered.Count > 0;

                foreach (var strategy in Strategies)
                {
                    var result = strategy.SelectZone(lot, gelType, zones, occupancy);

                    if (!hasCompatible)
                    {
                        // (d) No compatible zone with capacity => unslottable (Req 16.3).
                        Assert.True(
                            result.IsUnslottable,
                            $"{strategy.Key}: expected unslottable when no compatible zone with capacity exists.");
                        Assert.Equal(ErrorKind.Unslottable, result.Error.Kind);
                        continue;
                    }

                    // (a) Any selected zone is compatible and has capacity (Req 16.1).
                    Assert.True(result.IsSuccess, $"{strategy.Key}: expected a selection when a compatible zone exists.");
                    Assert.Contains(result.Zone, compatibleIds);

                    // (b) Determinism: a second selection over identical state + inputs is identical
                    // (Req 16.5).
                    var second = strategy.SelectZone(lot, gelType, zones, occupancy);
                    Assert.True(second.IsSuccess);
                    Assert.Equal(result.Zone, second.Zone);
                }

                if (hasCompatible)
                {
                    // (c) Tie-break by ascending zone id (Req 16.2). NaiveFirstAvailable always takes
                    // the smallest compatible zone id.
                    var naive = new NaiveFirstAvailableStrategy()
                        .SelectZone(lot, gelType, zones, occupancy);
                    Assert.True(naive.IsSuccess);
                    Assert.Equal(compatibleOrdered[0].Id, naive.Zone);

                    // Velocity-affinity minimizes velocity-weighted occupancy; among the zones that
                    // achieve that minimum it must pick the smallest zone id (ascending-id tie-break).
                    var velWeight = 1.0 + velocity;
                    var minCost = compatibleOrdered.Min(z => velWeight * z.StoredQuantity);
                    var expectedVelocityZone = compatibleOrdered
                        .Where(z => Math.Abs(velWeight * z.StoredQuantity - minCost) < 1e-9)
                        .OrderBy(z => z.Id)
                        .First()
                        .Id;

                    var affinity = new VelocityAffinitySlottingStrategy()
                        .SelectZone(lot, gelType, zones, occupancy);
                    Assert.True(affinity.IsSuccess);
                    Assert.Equal(expectedVelocityZone, affinity.Zone);
                }
            },
            iter: Iterations);
    }
}
