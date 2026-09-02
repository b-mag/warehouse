using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Fulfillment;
using Forge.Domain.Gels;

namespace Forge.Tests.Domain;

// Feature: nutrient-forge — unit tests for FEFO invalid requests and tie-breaks (task 6.3).
//
// Validates: Requirements 5.5 (invalid-request rejection), 5.2 (identical-expiry tie-break by
// FefoPriority then GelLotId), 28.1 (example/edge-case coverage). Also covers the zero-selectable
// partial case (Req 5.4).
public sealed class FefoSelectionTests
{
    private static readonly DateTimeOffset Now = new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly GelTypeId TypeA = new(new Guid("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly GelTypeId TypeB = new(new Guid("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    private static readonly TimeSpan NominalShelfLife = TimeSpan.FromDays(30);

    private static Formulation MakeFormulation() =>
        new(new TemperatureRange(-10m, 5m), NominalShelfLife, new[] { "vanilla" });

    private static GelLot Lot(
        GelLotId id,
        DateTimeOffset expiresAt,
        int quantity,
        int fefoPriority = 0,
        GelTypeId? gelType = null)
    {
        var producedAt = expiresAt - NominalShelfLife;
        return GelLot.Create(
            id,
            gelType ?? TypeA,
            MakeFormulation(),
            producedAt,
            quantity,
            fefoPriority,
            assignedZoneId: null);
    }

    private static GelLotId LotId(byte b) => new(new Guid(b, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    [Fact]
    public void Quantity_Below_One_Is_Rejected_With_InvalidRequest_And_Selects_Nothing()
    {
        var lots = new[] { Lot(LotId(1), Now.AddDays(5), quantity: 100) };

        var result = FefoSelector.Select(TypeA, requestedQuantity: 0, lots, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
        // Inventory is unchanged: the on-hand quantity is untouched by the pure query.
        Assert.Equal(100, lots[0].Quantity);
    }

    [Fact]
    public void Negative_Quantity_Is_Rejected_With_InvalidRequest()
    {
        var lots = new[] { Lot(LotId(1), Now.AddDays(5), quantity: 100) };

        var result = FefoSelector.Select(TypeA, requestedQuantity: -5, lots, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
    }

    [Fact]
    public void Quantity_Above_Max_Is_Rejected_With_InvalidRequest()
    {
        var lots = new[] { Lot(LotId(1), Now.AddDays(5), quantity: 100) };

        var result = FefoSelector.Select(
            TypeA,
            requestedQuantity: FefoSelector.MaxRequestedQuantity + 1,
            lots,
            Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
    }

    [Fact]
    public void Null_Lot_Collection_Is_Rejected_With_InvalidRequest()
    {
        var result = FefoSelector.Select(TypeA, requestedQuantity: 10, lots: null!, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidRequest, result.Error.Kind);
    }

    [Fact]
    public void Identical_Expiry_Ties_Are_Broken_By_FefoPriority_Then_LotId()
    {
        var expiry = Now.AddDays(5);

        // All three lots share the same expiry timestamp. Expected FEFO order:
        //   priority 1 (lot id 0x30) < priority 2 (lot id 0x10) < priority 2 (lot id 0x20).
        // i.e. FefoPriority ascending first, then GelLotId ascending for equal priorities.
        var lowPriority = Lot(LotId(0x30), expiry, quantity: 1, fefoPriority: 1);
        var highPriorityLowId = Lot(LotId(0x10), expiry, quantity: 1, fefoPriority: 2);
        var highPriorityHighId = Lot(LotId(0x20), expiry, quantity: 1, fefoPriority: 2);

        // Feed them in a scrambled order to prove ordering is by key, not insertion order.
        var lots = new[] { highPriorityHighId, lowPriority, highPriorityLowId };

        var result = FefoSelector.Select(TypeA, requestedQuantity: 3, lots, Now);

        Assert.True(result.IsSuccess);
        var selectedIds = result.Value.SelectedLots.Select(s => s.LotId).ToList();
        Assert.Equal(
            new[] { LotId(0x30), LotId(0x10), LotId(0x20) },
            selectedIds);
        Assert.Equal(3, result.Value.Fulfilled);
        Assert.Equal(0, result.Value.Shortfall);
        Assert.False(result.Value.IsPartial);
    }

    [Fact]
    public void All_Expired_Yields_Successful_Full_Shortfall_Partial()
    {
        // Two lots of the right type but flagged expired => zero selectable.
        var expiredA = Lot(LotId(1), Now.AddDays(5), quantity: 50);
        expiredA.TryExpireAt(expiredA.ExpiresAt.AddDays(10), out _);
        var expiredB = Lot(LotId(2), Now.AddDays(3), quantity: 50);
        expiredB.TryExpireAt(expiredB.ExpiresAt.AddDays(10), out _);

        var lots = new[] { expiredA, expiredB };

        var result = FefoSelector.Select(TypeA, requestedQuantity: 40, lots, Now);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.SelectedLots);
        Assert.Equal(0, result.Value.Fulfilled);
        Assert.Equal(40, result.Value.Shortfall);
        Assert.True(result.Value.IsPartial);
    }

    [Fact]
    public void No_Lots_Of_Requested_Type_Yields_Successful_Full_Shortfall_Partial()
    {
        // Inventory holds only TypeB lots; requesting TypeA yields zero selectable.
        var lots = new[]
        {
            Lot(LotId(1), Now.AddDays(5), quantity: 30, gelType: TypeB),
            Lot(LotId(2), Now.AddDays(6), quantity: 30, gelType: TypeB),
        };

        var result = FefoSelector.Select(TypeA, requestedQuantity: 25, lots, Now);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.SelectedLots);
        Assert.Equal(0, result.Value.Fulfilled);
        Assert.Equal(25, result.Value.Shortfall);
        Assert.True(result.Value.IsPartial);
    }
}
