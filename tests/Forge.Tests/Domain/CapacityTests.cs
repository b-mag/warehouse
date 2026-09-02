using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Vessels;

namespace Forge.Tests.Domain;

/// <summary>
/// Unit tests for capacity edge cases across zones and starships (task 8.3), covering the specific
/// boundaries the Property 4 invariant reasons about in general:
/// <list type="bullet">
///   <item><description>non-positive quantities are rejected and leave the quantity unchanged (Req 7.6);</description></item>
///   <item><description>the exact-capacity boundary is accepted while one more unit is rejected with
///   <see cref="ErrorKind.CapacityExceeded"/> reporting requested + remaining (Req 7.2, 7.4);</description></item>
///   <item><description>negative capacity configuration is rejected with
///   <see cref="ErrorKind.InvalidCapacity"/> (Req 7.7).</description></item>
/// </list>
/// <para><b>Validates: Requirements 7.6, 7.7, 28.1.</b></para>
/// </summary>
public sealed class CapacityTests
{
    private static readonly TemperatureRange Band = new(0m, 10m);

    private static readonly DateTimeOffset WindowStart = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2200, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static TemperatureZone NewZone(int capacity, int stored = 0)
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity, stored);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Starship NewStarship(int cargoCapacity, int loaded = 0)
    {
        var window = LoadingWindow.Create(WindowStart, WindowEnd);
        Assert.True(window.IsSuccess);

        var result = Starship.Create(StarshipId.New(), cargoCapacity, ColonyId.New(), new[] { window.Value }, loaded);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // ---- Non-positive quantity rejection (Req 7.6) ----

    [Fact]
    public void ZoneTryStore_WithZeroQuantity_IsRejected_LeavingStoredUnchanged()
    {
        var zone = NewZone(capacity: 10, stored: 3);

        var result = zone.TryStore(0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.CapacityExceeded, result.Error.Kind);
        Assert.Equal(3, zone.StoredQuantity);
        Assert.Equal(7, zone.RemainingCapacity);
    }

    [Fact]
    public void ZoneTryStore_WithNegativeQuantity_IsRejected_LeavingStoredUnchanged()
    {
        var zone = NewZone(capacity: 10, stored: 3);

        var result = zone.TryStore(-1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.CapacityExceeded, result.Error.Kind);
        Assert.Equal(3, zone.StoredQuantity);
        Assert.Equal(7, zone.RemainingCapacity);
    }

    [Fact]
    public void StarshipTryLoad_WithZeroQuantity_IsRejected_LeavingLoadedUnchanged()
    {
        var starship = NewStarship(cargoCapacity: 10, loaded: 4);

        var result = starship.TryLoad(0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.CapacityExceeded, result.Error.Kind);
        Assert.Equal(4, starship.LoadedQuantity);
        Assert.Equal(6, starship.RemainingCapacity);
    }

    // ---- Exact-capacity boundary accept, one-more reject (Req 7.2, 7.4) ----

    [Fact]
    public void ZoneTryStore_ExactlyRemainingCapacity_Succeeds_ThenOneMoreExceeds()
    {
        var zone = NewZone(capacity: 10, stored: 4);
        var remaining = zone.RemainingCapacity; // 6

        var exact = zone.TryStore(remaining);

        Assert.True(exact.IsSuccess);
        Assert.Equal(10, zone.StoredQuantity);
        Assert.Equal(0, zone.RemainingCapacity);

        var oneMore = zone.TryStore(1);

        Assert.True(oneMore.IsFailure);
        Assert.Equal(ErrorKind.CapacityExceeded, oneMore.Error.Kind);
        Assert.Equal(10, zone.StoredQuantity); // unchanged
        Assert.Equal(1, oneMore.Error.Detail!["requested"]);
        Assert.Equal(0, oneMore.Error.Detail!["remainingCapacity"]);
    }

    [Fact]
    public void StarshipTryLoad_ExactlyRemainingCapacity_Succeeds_ThenOneMoreExceeds()
    {
        var starship = NewStarship(cargoCapacity: 8, loaded: 2);
        var remaining = starship.RemainingCapacity; // 6

        var exact = starship.TryLoad(remaining);

        Assert.True(exact.IsSuccess);
        Assert.Equal(8, starship.LoadedQuantity);
        Assert.Equal(0, starship.RemainingCapacity);

        var oneMore = starship.TryLoad(1);

        Assert.True(oneMore.IsFailure);
        Assert.Equal(ErrorKind.CapacityExceeded, oneMore.Error.Kind);
        Assert.Equal(8, starship.LoadedQuantity); // unchanged
        Assert.Equal(1, oneMore.Error.Detail!["requested"]);
        Assert.Equal(0, oneMore.Error.Detail!["remainingCapacity"]);
    }

    // ---- Negative capacity configuration rejection (Req 7.7, 28.1) ----

    [Fact]
    public void TemperatureZoneCreate_WithNegativeCapacity_IsRejectedAsInvalidCapacity()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: -1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidCapacity, result.Error.Kind);
    }

    [Fact]
    public void StarshipCreate_WithNegativeCargoCapacity_IsRejectedAsInvalidCapacity()
    {
        var window = LoadingWindow.Create(WindowStart, WindowEnd);
        Assert.True(window.IsSuccess);

        var result = Starship.Create(StarshipId.New(), cargoCapacity: -1, ColonyId.New(), new[] { window.Value });

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidCapacity, result.Error.Kind);
    }
}
