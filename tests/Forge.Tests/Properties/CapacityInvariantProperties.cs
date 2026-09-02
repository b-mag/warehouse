using CsCheck;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Vessels;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 4: Capacity invariant for zones and starships
/// <summary>
/// Property 4 (design.md): <em>For any</em> sequence of put-away or load operations against a zone
/// or starship, every accepted operation SHALL leave the stored/loaded quantity less than or equal
/// to the capacity, every rejected operation (would-exceed or non-positive quantity) SHALL leave the
/// stored/loaded quantity unchanged, and remaining capacity SHALL always equal
/// <c>capacity − stored/loaded</c>.
/// <para>
/// The zone (<see cref="TemperatureZone"/>) and the starship (<see cref="Starship"/>) share the same
/// <c>CapacityRule</c>, so the invariant is asserted identically against both aggregate kinds.
/// </para>
/// <para><b>Validates: Requirements 7.1, 7.2, 7.3, 7.4, 7.5, 13.5.</b></para>
/// </summary>
public sealed class CapacityInvariantProperties
{
    private const int Iterations = 100;

    /// <summary>The kind of operation applied to an aggregate during a sequence.</summary>
    private enum OpKind
    {
        /// <summary>TryStore (zone) / TryLoad (starship) — an additive capacity operation.</summary>
        Add,

        /// <summary>TryRemove (zone only) — a subtractive capacity operation.</summary>
        Remove,
    }

    private readonly record struct Op(OpKind Kind, int Quantity);

    // A quantity generator that intentionally spans invalid (non-positive) values, small valid
    // values, and large values that will frequently would-exceed the capacity, so every branch of
    // the capacity rule is exercised.
    private static readonly Gen<int> GenQuantity =
        Gen.Int[-5, 120];

    private static readonly Gen<Op> GenOp =
        Gen.OneOf(
            GenQuantity.Select(q => new Op(OpKind.Add, q)),
            GenQuantity.Select(q => new Op(OpKind.Remove, q)));

    // 0..30 operations per sequence.
    private static readonly Gen<Op[]> GenOps = GenOp.Array[0, 30];

    // Valid zone capacity is 1..100000 (Req 6.1); constrain to a modest band so would-exceed is
    // reachable within the quantity range above.
    private static readonly Gen<int> GenZoneCapacity = Gen.Int[1, 100];

    // Valid starship capacity is >= 0 (Req 13.1 / 7.7); include 0 so the empty-capacity edge holds.
    private static readonly Gen<int> GenStarshipCapacity = Gen.Int[0, 100];

    [Fact]
    public void ZoneCapacityInvariantHoldsAcrossOperationSequence()
    {
        Gen.Select(GenZoneCapacity, GenOps)
            .Sample((capacity, ops) =>
            {
                var created = TemperatureZone.Create(ZoneId.New(), new TemperatureRange(0m, 10m), capacity);
                Assert.True(created.IsSuccess);
                var zone = created.Value;

                // Invariant holds at the initial state before any operation.
                AssertRemainingCapacityInvariant(zone.Capacity, zone.StoredQuantity, zone.RemainingCapacity);

                foreach (var op in ops)
                {
                    var before = zone.StoredQuantity;

                    var result = op.Kind == OpKind.Add
                        ? zone.TryStore(op.Quantity)
                        : zone.TryRemove(op.Quantity);

                    AssertOperationInvariant(
                        op,
                        result,
                        before,
                        zone.StoredQuantity,
                        zone.Capacity,
                        zone.RemainingCapacity);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void StarshipCapacityInvariantHoldsAcrossOperationSequence()
    {
        Gen.Select(GenStarshipCapacity, GenOps)
            .Sample((capacity, ops) =>
            {
                var window = LoadingWindow.Create(
                    new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    new DateTimeOffset(2200, 1, 2, 0, 0, 0, TimeSpan.Zero));
                Assert.True(window.IsSuccess);

                var created = Starship.Create(
                    StarshipId.New(),
                    capacity,
                    ColonyId.New(),
                    new[] { window.Value });
                Assert.True(created.IsSuccess);
                var starship = created.Value;

                AssertRemainingCapacityInvariant(
                    starship.CargoCapacity,
                    starship.LoadedQuantity,
                    starship.RemainingCapacity);

                foreach (var op in ops)
                {
                    // A starship only loads (adds). Treat Remove ops as loads too so the shared
                    // capacity rule is exercised the same way on both aggregate kinds.
                    var before = starship.LoadedQuantity;

                    var result = starship.TryLoad(op.Quantity);

                    AssertOperationInvariant(
                        op with { Kind = OpKind.Add },
                        result,
                        before,
                        starship.LoadedQuantity,
                        starship.CargoCapacity,
                        starship.RemainingCapacity);
                }
            }, iter: Iterations);
    }

    private static void AssertOperationInvariant(
        Op op,
        Result result,
        int quantityBefore,
        int quantityAfter,
        int capacity,
        int remainingCapacity)
    {
        if (result.IsSuccess)
        {
            // Accepted op: quantity stays within [0, capacity].
            Assert.True(quantityAfter >= 0, $"Accepted {op.Kind} left quantity negative: {quantityAfter}.");
            Assert.True(
                quantityAfter <= capacity,
                $"Accepted {op.Kind} left quantity {quantityAfter} above capacity {capacity}.");

            var expected = op.Kind == OpKind.Add
                ? quantityBefore + op.Quantity
                : quantityBefore - op.Quantity;
            Assert.Equal(expected, quantityAfter);
        }
        else
        {
            // Rejected op (would-exceed or non-positive quantity): quantity is unchanged.
            Assert.Equal(quantityBefore, quantityAfter);
        }

        // Remaining capacity == capacity − stored/loaded at all times.
        AssertRemainingCapacityInvariant(capacity, quantityAfter, remainingCapacity);
    }

    private static void AssertRemainingCapacityInvariant(int capacity, int quantity, int remainingCapacity)
    {
        Assert.Equal(capacity - quantity, remainingCapacity);
    }
}
