using CsCheck;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Fulfillment;
using Forge.Domain.Gels;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 1: FEFO selection ordering, accumulation, shortfall, and determinism
//
// Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.6, 13.4, 28.5
//
// For any inventory of gel lots and any valid fulfillment/load request (integer quantity in
// 1..999,999,999), FefoSelector.Select SHALL:
//   - include only non-expired lots whose ExpiresAt is strictly greater than now (Req 5.1);
//   - order the selection ascending by (ExpiresAt, FefoPriority, GelLotId) (Req 5.2);
//   - accumulate lots until the request is met or no selectable lots remain, partial-filling the
//     last lot and never over-selecting (Req 5.3);
//   - report Fulfilled = min(requested, total selectable), Shortfall = requested - Fulfilled, and
//     IsPartial iff Shortfall > 0 (Req 5.4, 13.4);
//   - produce an identical ordered selection for two runs over identical inventory + inputs (Req 5.6).
public sealed class FefoSelectionProperties
{
    // A single fixed "now"; lot expiry timestamps are generated relative to it so we control the
    // expired / non-expired mix deterministically.
    private static readonly DateTimeOffset Now = new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // The gel type under test. Every generated inventory targets this type; the generator also mixes
    // in some lots of an unrelated type to prove the selector filters by gel type.
    private static readonly GelTypeId TargetType = new(new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly GelTypeId OtherType = new(new Guid("22222222-2222-2222-2222-222222222222"));

    // Storage range + shelf life are irrelevant to FEFO ordering, so a fixed formulation suffices.
    // We build lots by choosing ProducedAt so that ProducedAt + NominalShelfLife == the desired expiry.
    private static readonly TimeSpan NominalShelfLife = TimeSpan.FromDays(30);

    /// <summary>
    /// A blueprint for a lot: the raw fields we generate, kept separate from the constructed
    /// <see cref="GelLot"/> so the generator stays simple and the lot is built via the domain factory.
    /// </summary>
    private readonly record struct LotSpec(
        GelLotId Id,
        GelTypeId GelTypeId,
        int ExpiryOffsetSeconds,
        int FefoPriority,
        int Quantity,
        bool MarkExpired);

    private static Formulation MakeFormulation() =>
        new(new TemperatureRange(-10m, 5m), NominalShelfLife, new[] { "vanilla" });

    private static GelLot Build(LotSpec spec)
    {
        var expiresAt = Now + TimeSpan.FromSeconds(spec.ExpiryOffsetSeconds);
        var producedAt = expiresAt - NominalShelfLife;

        var lot = GelLot.Create(
            spec.Id,
            spec.GelTypeId,
            MakeFormulation(),
            producedAt,
            spec.Quantity,
            spec.FefoPriority,
            assignedZoneId: null);

        // A lot can be flagged expired even though its ExpiresAt is still in the future; the selector
        // must exclude it via the IsExpired flag independent of the timestamp cutoff. TryExpireAt only
        // transitions when whole-second remaining <= 0, so force it by evaluating past expiry.
        if (spec.MarkExpired)
        {
            lot.TryExpireAt(expiresAt + TimeSpan.FromSeconds(1), out _);
        }

        return lot;
    }

    // Distinct GUIDs per lot so the (ExpiresAt, FefoPriority, GelLotId) key is fully determined and the
    // id tie-break is exercised. A sequential index maps to a stable GUID.
    private static readonly Gen<Guid> GenGuid =
        Gen.Int[0, 100_000].Select(i =>
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(i).CopyTo(bytes, 0);
            return new Guid(bytes);
        });

    private static readonly Gen<LotSpec> GenLotSpec =
        from id in GenGuid
        from isTarget in Gen.Bool
            // Expiry offset spans past (expired-by-time), zero (== now, must be excluded since strictly >),
            // and future. A small range with many collisions exercises the ordering tie-breaks.
        from expiry in Gen.Int[-50, 50]
        from priority in Gen.Int[0, 4]   // small range => frequent FefoPriority ties
        from qty in Gen.Int[0, 25]       // include 0 (must be excluded)
        from expired in Gen.Bool
        select new LotSpec(
            new GelLotId(id),
            isTarget ? TargetType : OtherType,
            expiry,
            priority,
            qty,
            expired);

    // Ensure ids are unique within an inventory so ordering is total; dedup by id after generation.
    private static readonly Gen<List<LotSpec>> GenInventory =
        GenLotSpec.List[0, 30].Select(list =>
            list
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList());

    // Requested quantity across the valid range 1..MaxRequestedQuantity, weighted toward small values
    // (which are met by the generated inventories) while still occasionally probing the whole range.
    private static readonly Gen<int> GenRequested =
        Gen.Frequency(
            (8, Gen.Int[1, 200]),
            (1, Gen.Int[FefoSelector.MinRequestedQuantity, FefoSelector.MaxRequestedQuantity]));

    [Fact]
    public void FefoSelection_Ordering_Accumulation_Shortfall_And_Determinism()
    {
        Gen.Select(GenInventory, GenRequested)
            .Sample((specs, requested) =>
            {
                var lots = specs.Select(Build).ToList();

                var result = FefoSelector.Select(TargetType, requested, lots, Now);

                // A valid request always succeeds (full, partial, or zero-selectable).
                Assert.True(result.IsSuccess);
                var fulfillment = result.Value;

                // Ground-truth selectable set: target type, not expired flag, ExpiresAt strictly > now,
                // quantity > 0 — ordered by the deterministic FEFO key.
                var expectedOrder = lots
                    .Where(l => l.GelTypeId.Equals(TargetType)
                        && !l.IsExpired
                        && l.ExpiresAt > Now
                        && l.Quantity > 0)
                    .OrderBy(l => l.ExpiresAt)
                    .ThenBy(l => l.FefoPriority)
                    .ThenBy(l => l.Id)
                    .ToList();

                var lotById = lots.ToDictionary(l => l.Id);

                // Req 5.1: every selected lot is non-expired with ExpiresAt strictly > now, right type,
                // and positive on-hand quantity.
                foreach (var sel in fulfillment.SelectedLots)
                {
                    var lot = lotById[sel.LotId];
                    Assert.Equal(TargetType, lot.GelTypeId);
                    Assert.False(lot.IsExpired);
                    Assert.True(lot.ExpiresAt > Now);
                    Assert.True(lot.Quantity > 0);
                }

                // Req 5.2 / 5.3: the selected lots are a prefix of the fully ordered selectable set.
                var selectedIds = fulfillment.SelectedLots.Select(s => s.LotId).ToList();
                var expectedPrefixIds = expectedOrder.Take(selectedIds.Count).Select(l => l.Id).ToList();
                Assert.Equal(expectedPrefixIds, selectedIds);

                var totalSelectable = expectedOrder.Sum(l => l.Quantity);
                var expectedFulfilled = Math.Min(requested, totalSelectable);

                // Req 5.3: no over-selection. Each entry except possibly the last takes the lot's full
                // quantity; the last may be a partial fill; per-lot take never exceeds the lot's stock.
                for (var i = 0; i < fulfillment.SelectedLots.Count; i++)
                {
                    var sel = fulfillment.SelectedLots[i];
                    var lot = lotById[sel.LotId];
                    Assert.True(sel.Quantity > 0);
                    Assert.True(sel.Quantity <= lot.Quantity);
                    if (i < fulfillment.SelectedLots.Count - 1)
                    {
                        Assert.Equal(lot.Quantity, sel.Quantity);
                    }
                }

                // Req 5.3 / 5.4: cumulative selected == min(requested, total selectable); accumulation
                // stops exactly when the request is met or lots are exhausted.
                var cumulative = fulfillment.SelectedLots.Sum(s => s.Quantity);
                Assert.Equal(expectedFulfilled, cumulative);
                Assert.Equal(expectedFulfilled, fulfillment.Fulfilled);

                // Req 5.4 / 13.4: shortfall and partial flag.
                Assert.Equal(requested - expectedFulfilled, fulfillment.Shortfall);
                Assert.True(fulfillment.Shortfall >= 0);
                Assert.Equal(fulfillment.Shortfall > 0, fulfillment.IsPartial);

                // If the request was fully met, we must not have consumed more lots than needed:
                // dropping the last selected lot would leave the cumulative below the request.
                if (fulfillment.Shortfall == 0 && fulfillment.SelectedLots.Count > 0)
                {
                    var withoutLast = fulfillment.SelectedLots
                        .Take(fulfillment.SelectedLots.Count - 1)
                        .Sum(s => s.Quantity);
                    Assert.True(withoutLast < requested);
                }

                // Req 5.6 / 28.5: determinism — a second call over identical inventory + inputs yields an
                // identical ordered selection and identical quantities.
                var second = FefoSelector.Select(TargetType, requested, lots, Now);
                Assert.True(second.IsSuccess);
                Assert.Equal(fulfillment.Fulfilled, second.Value.Fulfilled);
                Assert.Equal(fulfillment.Shortfall, second.Value.Shortfall);
                Assert.Equal(fulfillment.IsPartial, second.Value.IsPartial);
                Assert.Equal(
                    fulfillment.SelectedLots.ToList(),
                    second.Value.SelectedLots.ToList());
            },
            iter: 100);
    }
}
