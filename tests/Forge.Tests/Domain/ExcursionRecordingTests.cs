using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Tests.Domain;

/// <summary>
/// Unit tests for the pure cold-chain recording rule
/// <see cref="GelLot.RecordTemperature(TemperatureReading, TemperatureRange, out Forge.Domain.Events.TemperatureExcursion?)"/>
/// added for task 24.3. These cover the lot-side behavior that the earlier excursion-detection
/// tests deferred: zone-less rejection, in-range append with no excursion, out-of-range excursion
/// + at-risk flagging, and timestamp-ordered history.
/// <para>
/// Validates: Requirements 6.2, 6.3, 6.4, 28.1.
/// </para>
/// </summary>
public sealed class ExcursionRecordingTests
{
    // A representative allowable band [2, 8] °C, matching the cold-chain excursion tests.
    private static readonly TemperatureRange Band = new(2m, 8m);

    private static readonly DateTimeOffset T0 = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static GelType SampleGelType() =>
        new(GelTypeId.New(),
            new Formulation(Band, TimeSpan.FromDays(30), new[] { "vanilla" }),
            velocity: 1.0);

    private static GelLot LotWithZone() =>
        GelLot.Create(GelLotId.New(), SampleGelType(), T0, quantity: 10, assignedZoneId: ZoneId.New());

    private static GelLot LotWithoutZone() =>
        GelLot.Create(GelLotId.New(), SampleGelType(), T0, quantity: 10, assignedZoneId: null);

    // ---- Req 6.4: zone-less lot rejection ----

    [Fact]
    public void RecordTemperature_OnZoneLessLot_IsRejectedWithNoAssignedZone()
    {
        var lot = LotWithoutZone();

        var result = lot.RecordTemperature(new TemperatureReading(5m, T0), Band, out var excursion);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.NoAssignedZone, result.Error.Kind);
        Assert.Null(excursion);
    }

    [Fact]
    public void RecordTemperature_OnZoneLessLot_LeavesLotUnchanged()
    {
        var lot = LotWithoutZone();

        lot.RecordTemperature(new TemperatureReading(100m, T0), Band, out _);

        Assert.Empty(lot.TemperatureHistory);
        Assert.False(lot.AtRisk);
    }

    // ---- Req 6.2 / 6.3: in-range reading appended, no excursion, not at-risk ----

    [Fact]
    public void RecordTemperature_InRange_AppendsReadingWithNoExcursionAndNoRisk()
    {
        var lot = LotWithZone();
        var reading = new TemperatureReading(5m, T0);

        var result = lot.RecordTemperature(reading, Band, out var excursion);

        Assert.True(result.IsSuccess);
        Assert.Null(excursion);
        Assert.False(lot.AtRisk);
        Assert.Single(lot.TemperatureHistory);
        Assert.Equal(reading, lot.TemperatureHistory[0]);
    }

    [Fact]
    public void RecordTemperature_ExactlyOnInclusiveBounds_IsNotAnExcursion()
    {
        var lot = LotWithZone();

        var atMin = lot.RecordTemperature(new TemperatureReading(2m, T0), Band, out var minExcursion);
        var atMax = lot.RecordTemperature(new TemperatureReading(8m, T0.AddMinutes(1)), Band, out var maxExcursion);

        Assert.True(atMin.IsSuccess);
        Assert.True(atMax.IsSuccess);
        Assert.Null(minExcursion);
        Assert.Null(maxExcursion);
        Assert.False(lot.AtRisk);
        Assert.Equal(2, lot.TemperatureHistory.Count);
    }

    // ---- Req 6.3: out-of-range reading raises excursion + sets at-risk ----

    [Fact]
    public void RecordTemperature_AboveMaximum_RaisesExcursionAndSetsAtRisk()
    {
        var lot = LotWithZone();
        var reading = new TemperatureReading(9.5m, T0);

        var result = lot.RecordTemperature(reading, Band, out var excursion);

        Assert.True(result.IsSuccess);
        Assert.True(lot.AtRisk);
        Assert.NotNull(excursion);
        Assert.Equal(lot.Id, excursion!.LotId);
        Assert.Equal(9.5m, excursion.Celsius);
        Assert.Equal(T0, excursion.At);
        Assert.Single(lot.TemperatureHistory);
    }

    [Fact]
    public void RecordTemperature_BelowMinimum_RaisesExcursionAndSetsAtRisk()
    {
        var lot = LotWithZone();

        var result = lot.RecordTemperature(new TemperatureReading(-1m, T0), Band, out var excursion);

        Assert.True(result.IsSuccess);
        Assert.True(lot.AtRisk);
        Assert.NotNull(excursion);
        Assert.Equal(-1m, excursion!.Celsius);
    }

    [Fact]
    public void RecordTemperature_InRangeAfterExcursion_KeepsAtRiskSet()
    {
        var lot = LotWithZone();

        // First an excursion flags the lot at-risk...
        lot.RecordTemperature(new TemperatureReading(20m, T0), Band, out _);
        Assert.True(lot.AtRisk);

        // ...a subsequent in-range reading does NOT clear the unresolved excursion (Req 6.5).
        var result = lot.RecordTemperature(new TemperatureReading(5m, T0.AddMinutes(1)), Band, out var excursion);

        Assert.True(result.IsSuccess);
        Assert.Null(excursion);
        Assert.True(lot.AtRisk);
    }

    // ---- Req 6.2: history stays ordered by timestamp regardless of arrival order ----

    [Fact]
    public void RecordTemperature_OutOfOrderArrivals_HistoryIsTimestampOrdered()
    {
        var lot = LotWithZone();

        var third = new TemperatureReading(5m, T0.AddMinutes(30));
        var first = new TemperatureReading(5m, T0);
        var second = new TemperatureReading(5m, T0.AddMinutes(10));

        // Record deliberately out of chronological order.
        lot.RecordTemperature(third, Band, out _);
        lot.RecordTemperature(first, Band, out _);
        lot.RecordTemperature(second, Band, out _);

        Assert.Equal(3, lot.TemperatureHistory.Count);
        Assert.Equal(first.At, lot.TemperatureHistory[0].At);
        Assert.Equal(second.At, lot.TemperatureHistory[1].At);
        Assert.Equal(third.At, lot.TemperatureHistory[2].At);

        // The history is non-decreasing in timestamp.
        for (var i = 1; i < lot.TemperatureHistory.Count; i++)
        {
            Assert.True(lot.TemperatureHistory[i - 1].At <= lot.TemperatureHistory[i].At);
        }
    }
}
