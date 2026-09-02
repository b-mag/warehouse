using CsCheck;

using Forge.Application.Labor;

using Forge.Domain.Common;
using Forge.Domain.Labor;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;

using TaskStatus = Forge.Domain.Tasks.TaskStatus;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 8: Shift-gated worker assignment
//
// Property 8 (design.md): "For any worker and any current simulated time, the worker SHALL be
// assignable to a task if and only if the current time falls within one of that worker's shifts."
//
// Validates: Requirements 15.5
//
// The System Under Test is the Application WorkerAssignmentService (task 19.1), whose shift gate is
// Worker.IsOnShift (inclusive bounds). The property checks two directions:
//   * single worker: assignable  <=>  now within some shift of that worker;
//   * worker pool: a task is assigned iff at least one worker is on shift, and the selected worker is
//     both on shift and the deterministic lowest-WorkerId choice.
public sealed class ShiftAssignmentProperties
{
    // >=100 iterations required by the spec.
    private const int Iterations = 100;

    // A fixed epoch; shift bounds and "now" are whole-second offsets from it so the inclusive-bounds
    // gate is exercised exactly, including moments on a shift boundary.
    private static readonly DateTimeOffset Epoch = new(2350, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly WorkerAssignmentService Service = new();

    // A shift blueprint: a start offset (seconds from Epoch) and a strictly-positive duration so the
    // domain factory (which requires End > Start) always accepts it.
    private readonly record struct ShiftSpec(int StartOffsetSeconds, int DurationSeconds);

    private static readonly Gen<ShiftSpec> GenShiftSpec =
        from start in Gen.Int[0, 100_000]
        from duration in Gen.Int[1, 20_000]
        select new ShiftSpec(start, duration);

    // 1..4 shifts per worker (Req 15.1 requires at least one).
    private static readonly Gen<ShiftSpec[]> GenShiftSpecs = GenShiftSpec.Array[1, 4];

    // "now" spans well before, within, on the boundaries of, and well after the generated shifts so
    // both on-shift and off-shift cases are reached frequently.
    private static readonly Gen<int> GenNowOffset = Gen.Int[-20_000, 140_000];

    private static Worker BuildWorker(WorkerId id, ShiftSpec[] specs)
    {
        var shifts = new List<WorkerShift>();
        foreach (var spec in specs)
        {
            var start = Epoch + TimeSpan.FromSeconds(spec.StartOffsetSeconds);
            var end = start + TimeSpan.FromSeconds(spec.DurationSeconds);
            var created = WorkerShift.Create(start, end);
            Assert.True(created.IsSuccess);
            shifts.Add(created.Value);
        }

        var worker = Worker.Create(id, hourlyRate: 20m, shifts);
        Assert.True(worker.IsSuccess);
        return worker.Value;
    }

    // A fresh Created task each attempt so AssignTo (Created -> Assigned) is always a valid transition
    // and the outcome is driven purely by the shift gate, not the task lifecycle.
    private static WarehouseTask BuildTask()
    {
        var created = WarehouseTask.Create(
            WarehouseTaskId.New(),
            WarehouseTaskType.Pick,
            new Cell(0, 0),
            new Cell(1, 1),
            TimeSpan.FromMinutes(5));
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    // Req 15.5: a single worker is assignable IFF now is within one of that worker's shifts.
    [Fact]
    public void SingleWorkerAssignableIffNowWithinAShift()
    {
        Gen.Select(GenShiftSpecs, GenNowOffset)
            .Sample((specs, nowOffset) =>
            {
                var now = Epoch + TimeSpan.FromSeconds(nowOffset);
                var worker = BuildWorker(WorkerId.New(), specs);

                // Ground truth computed independently of the SUT: is now inside [Start, End] inclusive?
                var expectedOnShift = specs.Any(s =>
                {
                    var start = Epoch + TimeSpan.FromSeconds(s.StartOffsetSeconds);
                    var end = start + TimeSpan.FromSeconds(s.DurationSeconds);
                    return now >= start && now <= end;
                });

                var task = BuildTask();
                var outcome = Service.Assign(task, new[] { worker }, now);

                if (expectedOnShift)
                {
                    Assert.True(outcome.IsAssigned);
                    Assert.Equal(worker.Id, outcome.Worker);
                    Assert.Equal(worker.Id, task.AssignedWorker);
                    Assert.Equal(TaskStatus.Assigned, task.Status);
                }
                else
                {
                    // No worker on shift: task left queued for the backlog, state unchanged.
                    Assert.True(outcome.IsNoWorkerAvailable);
                    Assert.Null(task.AssignedWorker);
                    Assert.Equal(TaskStatus.Created, task.Status);
                }
            }, iter: Iterations);
    }

    // Req 15.5: across a pool, a task is assigned iff SOME worker is on shift; the chosen worker is on
    // shift and is the deterministic lowest-WorkerId among the on-shift set.
    [Fact]
    public void PoolAssignsIffAnyWorkerOnShift_SelectsLowestIdOnShift()
    {
        var genPool = GenShiftSpecs.List[1, 5];

        Gen.Select(genPool, GenNowOffset)
            .Sample((pool, nowOffset) =>
            {
                var now = Epoch + TimeSpan.FromSeconds(nowOffset);
                var workers = pool.Select(specs => BuildWorker(WorkerId.New(), specs)).ToList();

                var onShift = workers.Where(w => w.IsOnShift(now)).ToList();
                var task = BuildTask();
                var outcome = Service.Assign(task, workers, now);

                if (onShift.Count == 0)
                {
                    Assert.True(outcome.IsNoWorkerAvailable);
                    Assert.Null(task.AssignedWorker);
                    Assert.Equal(TaskStatus.Created, task.Status);
                }
                else
                {
                    Assert.True(outcome.IsAssigned);

                    // Deterministic selection: lowest WorkerId among the on-shift workers.
                    var expected = onShift.OrderBy(w => w.Id).First().Id;
                    Assert.Equal(expected, outcome.Worker);
                    Assert.Equal(expected, task.AssignedWorker);
                    // The selected worker really is on shift at now.
                    Assert.True(workers.Single(w => w.Id.Equals(outcome.Worker!.Value)).IsOnShift(now));
                }
            }, iter: Iterations);
    }
}
