using Forge.Domain.Common;
using Forge.Domain.Docks;

namespace Forge.Tests.Domain;

/// <summary>
/// Unit tests for the Docks domain data model (task 12.1): slot validation and pure
/// interval queries, schedule query helpers, and single-occupancy resource keying.
/// The scheduling/queue/utilization algorithm (task 20.1) and acquisition/queue
/// (task 15.2) are out of scope here.
/// </summary>
public sealed class DocksDomainTests
{
    private static readonly DateTimeOffset T0 = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int hours) => T0.AddHours(hours);

    // ---- DockSlot.Create validation ----

    [Fact]
    public void Create_WithEndAfterStart_Succeeds()
    {
        var result = DockSlot.Create(At(0), At(1), DockOperationKind.Inbound);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromHours(1), result.Value.Duration);
        Assert.Equal(DockOperationKind.Inbound, result.Value.Kind);
    }

    [Fact]
    public void Create_WithEndEqualToStart_IsRejected()
    {
        var result = DockSlot.Create(At(0), At(0), DockOperationKind.Outbound);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public void Create_WithEndBeforeStart_IsRejected()
    {
        var result = DockSlot.Create(At(2), At(1), DockOperationKind.Outbound);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    // ---- DockSlot interval queries (half-open [Start, End)) ----

    [Fact]
    public void Contains_IncludesStart_ExcludesEnd()
    {
        var slot = new DockSlot(At(1), At(3), DockOperationKind.Inbound);

        Assert.True(slot.Contains(At(1)));
        Assert.True(slot.Contains(At(2)));
        Assert.False(slot.Contains(At(3)));
        Assert.False(slot.Contains(At(0)));
    }

    [Fact]
    public void Overlaps_IsTrueForShared_FalseForAdjacent()
    {
        var a = new DockSlot(At(1), At(3), DockOperationKind.Inbound);
        var overlapping = new DockSlot(At(2), At(4), DockOperationKind.Outbound);
        var adjacent = new DockSlot(At(3), At(5), DockOperationKind.Inbound);

        Assert.True(a.Overlaps(overlapping));
        Assert.True(overlapping.Overlaps(a));
        Assert.False(a.Overlaps(adjacent));
        Assert.False(adjacent.Overlaps(a));
    }

    [Fact]
    public void HasEnded_IsTrueWhenEndAtOrBeforeNow()
    {
        var slot = new DockSlot(At(1), At(3), DockOperationKind.Inbound);

        Assert.True(slot.HasEnded(At(3)));
        Assert.True(slot.HasEnded(At(4)));
        Assert.False(slot.HasEnded(At(2)));
    }

    // ---- DockSchedule query helpers ----

    [Fact]
    public void Schedule_OrdersSlots_DeterministicallyRegardlessOfInputOrder()
    {
        var s1 = new DockSlot(At(0), At(1), DockOperationKind.Inbound);
        var s2 = new DockSlot(At(1), At(2), DockOperationKind.Outbound);
        var s3 = new DockSlot(At(2), At(3), DockOperationKind.Inbound);

        var a = new DockSchedule([s3, s1, s2]);
        var b = new DockSchedule([s2, s3, s1]);

        Assert.Equal(a.Slots, b.Slots);
        Assert.Equal([s1, s2, s3], a.Slots);
    }

    [Fact]
    public void SlotsAt_ReturnsSlotsContainingInstant()
    {
        var schedule = new DockSchedule(
        [
            new DockSlot(At(0), At(2), DockOperationKind.Inbound),
            new DockSlot(At(2), At(4), DockOperationKind.Outbound),
        ]);

        var atOne = schedule.SlotsAt(At(1));
        Assert.Single(atOne);
        Assert.Equal(DockOperationKind.Inbound, atOne[0].Kind);

        // Boundary: At(2) is excluded from the first slot, included in the second.
        var atTwo = schedule.SlotsAt(At(2));
        Assert.Single(atTwo);
        Assert.Equal(DockOperationKind.Outbound, atTwo[0].Kind);
    }

    [Fact]
    public void IsFree_TrueWhenNoOverlap_FalseWhenOverlap()
    {
        var schedule = new DockSchedule([new DockSlot(At(1), At(3), DockOperationKind.Inbound)]);

        Assert.True(schedule.IsFree(At(3), At(4)));   // adjacent, no overlap
        Assert.True(schedule.IsFree(At(0), At(1)));   // adjacent, no overlap
        Assert.False(schedule.IsFree(At(2), At(4)));  // overlaps
        Assert.True(schedule.IsFree(At(5), At(5)));   // empty interval is free
    }

    [Fact]
    public void NextSlotEndingAfter_ReturnsEarliestUnendedSlot_OrNull()
    {
        var schedule = new DockSchedule(
        [
            new DockSlot(At(0), At(2), DockOperationKind.Inbound),
            new DockSlot(At(3), At(5), DockOperationKind.Outbound),
        ]);

        var next = schedule.NextSlotEndingAfter(At(2));
        Assert.NotNull(next);
        Assert.Equal(At(5), next!.End);

        Assert.Null(schedule.NextSlotEndingAfter(At(5)));
    }

    [Fact]
    public void With_ReturnsNewSchedule_LeavingOriginalUnchanged()
    {
        var original = DockSchedule.Empty;
        var updated = original.With(new DockSlot(At(0), At(1), DockOperationKind.Inbound));

        Assert.Equal(0, original.Count);
        Assert.Equal(1, updated.Count);
    }

    // ---- DockBay / PickFace single-occupancy identity ----

    [Fact]
    public void DockBay_ExposesResourceId_KeyedByDockBayKind()
    {
        var id = DockBayId.New();
        var bay = new DockBay(id, isOpen: true);

        Assert.IsAssignableFrom<ISingleOccupancyResource>(bay);
        Assert.Equal(SingleOccupancyResourceKind.DockBay, bay.ResourceId.Kind);
        Assert.Equal(id.Value, bay.ResourceId.Value);
        Assert.Same(DockSchedule.Empty, bay.Schedule);
    }

    [Fact]
    public void PickFace_ExposesResourceId_KeyedByPickFaceKind()
    {
        var id = PickFaceId.New();
        var zone = ZoneId.New();
        var face = new PickFace(id, zone);

        Assert.IsAssignableFrom<ISingleOccupancyResource>(face);
        Assert.Equal(SingleOccupancyResourceKind.PickFace, face.ResourceId.Kind);
        Assert.Equal(id.Value, face.ResourceId.Value);
        Assert.Equal(zone, face.Zone);
    }

    [Fact]
    public void ResourceId_DistinguishesKinds_EvenWithSameGuid()
    {
        var guid = Guid.NewGuid();
        var dockKey = SingleOccupancyResourceId.ForDockBay(new DockBayId(guid));
        var faceKey = SingleOccupancyResourceId.ForPickFace(new PickFaceId(guid));

        Assert.NotEqual(dockKey, faceKey);
        Assert.NotEqual(0, dockKey.CompareTo(faceKey));
    }
}
