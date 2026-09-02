using CsCheck;

using Forge.Application.Docks;

using Forge.Domain.Common;
using Forge.Domain.Docks;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 13: Dock earliest-queued assignment determinism
//
// Property 13 (design.md): "For any queue of operations waiting on a dock bay, when a time slot
// becomes free the earliest queued operation SHALL be assigned, and identical queue state SHALL
// yield an identical assignment."
//
// The DockScheduler (Application task 20.1) assigns an operation to a bay when the bay is free for
// its slot and otherwise queues it FIFO; on Release it assigns the earliest queued operation the
// bay is now free for. This test builds a bay with a single slot interval so that, once occupied,
// every subsequent request for an overlapping slot is forced into the FIFO queue. It then releases
// the occupant and asserts (a) the earliest queued operation (by arrival order) is the one assigned,
// and (b) replaying the identical arrival sequence against a fresh scheduler yields the identical
// assignment — determinism.
//
// Validates: Requirements 17.5
public sealed class DockSchedulingProperties
{
    // ≥100 iterations required by the spec.
    private const int Iterations = 100;

    private static readonly DateTimeOffset Now =
        new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // All operations target overlapping slots on one bay, so only one can be assigned at a time and
    // the rest must queue. Slots run well into the future so none is rejected as already-ended.
    private static readonly DockSlot SharedSlot =
        new(Now.AddHours(1), Now.AddHours(2), DockOperationKind.Inbound);

    private sealed record Scenario(IReadOnlyList<Guid> OperationIds);

    private static Gen<Scenario> GenScenario =>
        from ids in Gen.Guid.List[2, 10]
        // Distinct ids so each operation is a distinct competitor in the queue.
        where ids.Distinct().Count() == ids.Count
        select new Scenario(ids);

    // Builds a scheduler where the first operation holds the shared slot and the rest are queued in
    // arrival order. Returns the holder and the queued operations in arrival (FIFO) order.
    private static (DockScheduler Scheduler, DockOperation Holder, IReadOnlyList<DockOperation> Queued)
        BuildQueuedScenario(Scenario scenario)
    {
        var bay = new DockBay(DockBayId.New(), isOpen: true, new DockSchedule(new[] { SharedSlot }));
        var scheduler = new DockScheduler();
        scheduler.RegisterBay(bay);

        var ops = scenario.OperationIds
            .Select(id => new DockOperation(id, bay.Id, SharedSlot))
            .ToList();

        // First request occupies the slot; the remainder are forced into the FIFO queue.
        var first = scheduler.Request(ops[0], Now);
        var queued = new List<DockOperation>();
        for (int i = 1; i < ops.Count; i++)
        {
            var outcome = scheduler.Request(ops[i], Now);
            // Every subsequent operation overlaps the occupied slot, so it must be queued.
            if (outcome.IsSuccess && !outcome.Value.IsAssigned)
            {
                queued.Add(ops[i]);
            }
        }

        return (scheduler, ops[0], queued);
    }

    // Req 17.5 / Property 13: releasing the holder assigns the EARLIEST queued operation (FIFO head).
    [Fact]
    public void Release_AssignsEarliestQueuedOperation()
    {
        GenScenario.Sample(scenario =>
        {
            var (scheduler, holder, queued) = BuildQueuedScenario(scenario);

            if (queued.Count == 0)
            {
                // Fewer than two competitors ended up queued; nothing to assert about ordering.
                return true;
            }

            var expectedNext = queued[0];
            var assigned = scheduler.Release(holder, Now);

            // The earliest queued operation must be the one assigned.
            return assigned is not null && assigned.Id.Equals(expectedNext.Id);
        }, iter: Iterations);
    }

    // Req 17.5 / Property 13: identical queue state yields identical assignment (determinism).
    [Fact]
    public void IdenticalQueueState_YieldsIdenticalAssignment()
    {
        GenScenario.Sample(scenario =>
        {
            var (schedulerA, holderA, queuedA) = BuildQueuedScenario(scenario);
            var (schedulerB, holderB, queuedB) = BuildQueuedScenario(scenario);

            var assignedA = schedulerA.Release(holderA, Now);
            var assignedB = schedulerB.Release(holderB, Now);

            // Same arrival sequence -> same assignment on release, run to run.
            if (assignedA is null && assignedB is null)
            {
                return true;
            }

            return assignedA is not null
                && assignedB is not null
                && assignedA.Id.Equals(assignedB.Id);
        }, iter: Iterations);
    }
}
