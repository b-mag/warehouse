using Forge.Domain.ColdChain;
using Forge.Domain.Common;

namespace Forge.Tests.Domain;

/// <summary>
/// Unit tests for excursion detection edge cases and temperature-zone capacity configuration
/// (task 7.3). These cover the pure domain primitives owned by the cold-chain subsystem:
/// inclusive-bound excursion detection on <see cref="TemperatureRange"/> and the validated
/// <see cref="TemperatureZone.Create"/> capacity guard.
/// <para>
/// Validates: Requirements 6.2, 6.4 (partial), 28.1. The handler-level parts of Req 6.2/6.4 —
/// appending readings to a lot's history in timestamp order and rejecting a recording for a
/// zone-less lot with <see cref="ErrorKind.NoAssignedZone"/> — live in the
/// RecordTemperatureReading handler (task 24.3) and are deferred to that task, not tested here.
/// </para>
/// </summary>
public sealed class ExcursionDetectionTests
{
    // Sample inclusive band [2, 8] °C.
    private static readonly TemperatureRange Band = new(2m, 8m);

    // ---- Inclusive-bound excursion edge cases (Req 6.3, boundaries of 6.1) ----

    [Fact]
    public void ValueExactlyAtMinimum_IsNotAnExcursion()
    {
        Assert.False(Band.IsExcursion(2m));
        Assert.True(Band.Contains(2m));
    }

    [Fact]
    public void ValueExactlyAtMaximum_IsNotAnExcursion()
    {
        Assert.False(Band.IsExcursion(8m));
        Assert.True(Band.Contains(8m));
    }

    [Fact]
    public void ValueJustBelowMinimum_IsAnExcursion()
    {
        Assert.True(Band.IsExcursion(1.99m));
        Assert.False(Band.Contains(1.99m));
    }

    [Fact]
    public void ValueJustAboveMaximum_IsAnExcursion()
    {
        Assert.True(Band.IsExcursion(8.01m));
        Assert.False(Band.Contains(8.01m));
    }

    [Fact]
    public void ValueWellInsideBand_IsNotAnExcursion()
    {
        Assert.False(Band.IsExcursion(5m));
    }

    [Fact]
    public void DegenerateBand_ExcursionOnlyOffTheSinglePoint()
    {
        var point = new TemperatureRange(4m, 4m);

        Assert.False(point.IsExcursion(4m));
        Assert.True(point.IsExcursion(3.99m));
        Assert.True(point.IsExcursion(4.01m));
    }

    // ---- TemperatureZone.Create capacity configuration (Req 6.1, 7.7, 28.1) ----

    [Fact]
    public void Create_WithCapacityZero_IsRejectedAsInvalidCapacity()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: 0);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidCapacity, result.Error.Kind);
    }

    [Fact]
    public void Create_WithNegativeCapacity_IsRejectedAsInvalidCapacity()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: -1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidCapacity, result.Error.Kind);
    }

    [Fact]
    public void Create_WithCapacityAtLowerBound_Succeeds()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: TemperatureZone.MinCapacity);

        Assert.True(result.IsSuccess);
        Assert.Equal(TemperatureZone.MinCapacity, result.Value.Capacity);
        Assert.Equal(TemperatureZone.MinCapacity, result.Value.RemainingCapacity);
    }

    [Fact]
    public void Create_WithCapacityAtUpperBound_Succeeds()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: TemperatureZone.MaxCapacity);

        Assert.True(result.IsSuccess);
        Assert.Equal(TemperatureZone.MaxCapacity, result.Value.Capacity);
    }

    [Fact]
    public void Create_WithCapacityWithinRange_Succeeds()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: 500);

        Assert.True(result.IsSuccess);
        Assert.Equal(500, result.Value.Capacity);
        Assert.Same(Band.GetType(), result.Value.AllowableRange.GetType());
        Assert.Equal(Band, result.Value.AllowableRange);
    }

    [Fact]
    public void Create_WithCapacityAboveUpperBound_IsRejectedAsInvalidCapacity()
    {
        var result = TemperatureZone.Create(ZoneId.New(), Band, capacity: TemperatureZone.MaxCapacity + 1);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.InvalidCapacity, result.Error.Kind);
    }
}
