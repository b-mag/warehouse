using Forge.Domain.Labor;
using Forge.Domain.Tasks;

namespace Forge.Application.Labor;

/// <summary>
/// Shift-gated worker assignment (Req 8.2, 8.3, 15.5, 15.7). Given the pool of workers and the
/// current simulated time, this service assigns a <see cref="WarehouseTask"/> to a worker
/// <b>only</b> when that worker is on shift at the current time (Req 15.5), and leaves the task
/// queued for the backlog when none is (Req 8.3, 15.7).
/// <para>
/// <b>Deterministic selection.</b> When several workers are on shift the service selects the one
/// with the <em>lowest</em> <see cref="WorkerId"/> (ascending order, via <c>WorkerId</c>'s
/// <see cref="IComparable{T}"/>). This mirrors the ascending-id tie-breaks used elsewhere in the
/// design (FEFO selection, slotting, reservation contention) so assignment is reproducible given
/// identical inputs — the same worker pool and the same "now" always pick the same worker.
/// </para>
/// <para>
/// The service depends only on the <see cref="Worker"/> domain model and the <see cref="WarehouseTask"/>
/// aggregate; it does not fetch or persist. Callers (the assignment flow / handlers, tasks 24.x)
/// supply the candidate workers — typically from <c>IWorkerRepository.GetOnShiftAsync</c> — and
/// persist the resulting task mutation. The shift predicate is re-checked here against the supplied
/// <paramref name="now"/> so the gate is enforced regardless of how the candidates were obtained.
/// </para>
/// </summary>
public sealed class WorkerAssignmentService
{
    /// <summary>
    /// Attempt to assign <paramref name="task"/> to an on-shift worker drawn from
    /// <paramref name="workers"/> at simulated time <paramref name="now"/> (Req 8.2, 8.3, 15.5, 15.7).
    /// <list type="bullet">
    ///   <item><description>Filters <paramref name="workers"/> to those on shift at
    ///   <paramref name="now"/> via <see cref="Worker.IsOnShift(DateTimeOffset)"/> (Req 15.5).</description></item>
    ///   <item><description>If none is on shift, returns
    ///   <see cref="WorkerAssignmentOutcome.NoWorkerAvailable"/> and leaves the task's state unchanged
    ///   so it stays queued for the backlog (Req 8.3, 15.7).</description></item>
    ///   <item><description>Otherwise selects the on-shift worker with the lowest
    ///   <see cref="WorkerId"/> (deterministic, Req 15.5) and calls
    ///   <see cref="WarehouseTask.AssignTo(WorkerId)"/>. A domain rejection (task not in an assignable
    ///   status) is surfaced as <see cref="WorkerAssignmentOutcome.Rejected"/> leaving state
    ///   unchanged; a success is reported as <see cref="WorkerAssignmentOutcome.Assigned"/> (Req 8.2).</description></item>
    /// </list>
    /// </summary>
    /// <param name="task">The task to assign; mutated only on a successful assignment.</param>
    /// <param name="workers">The candidate worker pool (any shift status; re-gated here).</param>
    /// <param name="now">The current simulated time the shift gate is evaluated against.</param>
    /// <returns>The shift-gated assignment outcome.</returns>
    public WorkerAssignmentOutcome Assign(
        WarehouseTask task,
        IReadOnlyList<Worker> workers,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(workers);

        var selected = SelectOnShiftWorker(workers, now);
        if (selected is null)
        {
            // No worker on shift: leave the task queued; the backlog logic (task 22.1) consumes this.
            return WorkerAssignmentOutcome.NoWorkerAvailable();
        }

        var result = task.AssignTo(selected.Id);
        return result.IsSuccess
            ? WorkerAssignmentOutcome.Assigned(selected.Id)
            : WorkerAssignmentOutcome.Rejected(result.Error);
    }

    /// <summary>
    /// Deterministically choose the assignable worker: among the workers on shift at
    /// <paramref name="now"/>, the one with the lowest <see cref="WorkerId"/> (Req 15.5). Returns
    /// <c>null</c> when no worker is on shift. Pure and side-effect free.
    /// </summary>
    /// <param name="workers">The candidate worker pool.</param>
    /// <param name="now">The current simulated time.</param>
    /// <returns>The selected on-shift worker, or <c>null</c> when none is on shift.</returns>
    public Worker? SelectOnShiftWorker(IReadOnlyList<Worker> workers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workers);

        Worker? best = null;
        foreach (var worker in workers)
        {
            if (!worker.IsOnShift(now))
            {
                continue;
            }

            // Ascending WorkerId wins, for a total, deterministic order across the on-shift set.
            if (best is null || worker.Id.CompareTo(best.Id) < 0)
            {
                best = worker;
            }
        }

        return best;
    }
}
