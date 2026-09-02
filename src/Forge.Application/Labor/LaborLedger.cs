using Forge.Domain.Common;

namespace Forge.Application.Labor;

/// <summary>
/// A worker's accrued labor so far: the total cost of the tasks they have completed and the total
/// time they spent on them (Req 15.6). Utilization is exposed as accrued busy time so a caller can
/// divide it by an on-shift window to obtain a fraction; keeping the raw busy time here avoids baking
/// a particular shift-window definition into the ledger.
/// </summary>
/// <param name="LaborCost">The worker's accrued labor cost (Req 15.3, 15.6).</param>
/// <param name="TasksCompleted">The number of tasks the worker has completed.</param>
/// <param name="BusyTime">The total (duration + travel) time the worker spent on completed tasks (Req 15.6).</param>
public readonly record struct WorkerUtilization(decimal LaborCost, int TasksCompleted, TimeSpan BusyTime);

/// <summary>
/// Accrues labor cost on task completion and exposes the total labor cost plus per-worker utilization
/// (Req 15.3, 15.6, 15.9). This is the small aggregate the completion flow (task 24.5) drives: when a
/// task an on-shift worker was assigned completes, the flow calls <see cref="AccrueOnCompletion"/> with
/// the task's duration, its derived travel time, and the worker's hourly rate.
/// <para>
/// <b>Deterministic accrual.</b> Each accrual delegates the arithmetic to
/// <see cref="LaborCostCalculator.ComputeLaborCost"/>, which computes
/// <c>(duration + travel) × rate</c> exactly in <see cref="decimal"/>. Because the ledger only sums
/// those exact per-task amounts, two tasks with identical (duration, travel, rate) accrue an identical
/// amount, and the running total is a deterministic function of the sequence of accruals (Req 15.9).
/// </para>
/// <para>
/// The ledger holds in-memory state and is not thread-safe; the tick pipeline applies rules on a
/// single logical thread, matching the other Application services here.
/// </para>
/// </summary>
public sealed class LaborLedger
{
    private sealed class Accrual
    {
        public decimal LaborCost;
        public int TasksCompleted;
        public TimeSpan BusyTime;
    }

    private readonly Dictionary<WorkerId, Accrual> _perWorker = new();

    /// <summary>The accrued total labor cost across every completed task (Req 15.6). Always <c>&gt;= 0</c>.</summary>
    public decimal TotalLaborCost { get; private set; }

    /// <summary>
    /// Accrue the labor cost of a completed task to <paramref name="worker"/> and to the running total
    /// (Req 15.3, 15.6, 15.9). The cost is <c>(duration + travelTime) × hourlyRate</c> computed exactly
    /// by <see cref="LaborCostCalculator"/>. Returns the amount accrued for this task so the caller can
    /// include it in a completion event.
    /// </summary>
    /// <param name="worker">The worker credited with completing the task.</param>
    /// <param name="duration">The task's estimated duration (Req 15.2).</param>
    /// <param name="travelTime">The task's derived travel time (Req 15.4).</param>
    /// <param name="hourlyRate">The worker's hourly rate (Req 15.1).</param>
    /// <returns>The labor cost accrued for this single task.</returns>
    public decimal AccrueOnCompletion(
        WorkerId worker,
        TimeSpan duration,
        TimeSpan travelTime,
        decimal hourlyRate)
    {
        decimal cost = LaborCostCalculator.ComputeLaborCost(duration, travelTime, hourlyRate);

        if (!_perWorker.TryGetValue(worker, out var accrual))
        {
            accrual = new Accrual();
            _perWorker[worker] = accrual;
        }

        accrual.LaborCost += cost;
        accrual.TasksCompleted += 1;
        // Busy time uses the same combined span the cost is based on. Clamp a negative combined span
        // (not expected given the domain's non-negative guarantees) to zero.
        var busy = duration + travelTime;
        if (busy > TimeSpan.Zero)
        {
            accrual.BusyTime += busy;
        }

        TotalLaborCost += cost;
        return cost;
    }

    /// <summary>
    /// The accrued utilization for <paramref name="worker"/> (Req 15.6): cost, completed-task count,
    /// and busy time. Returns a zero utilization for a worker who has completed nothing.
    /// </summary>
    public WorkerUtilization UtilizationFor(WorkerId worker) =>
        _perWorker.TryGetValue(worker, out var a)
            ? new WorkerUtilization(a.LaborCost, a.TasksCompleted, a.BusyTime)
            : new WorkerUtilization(0m, 0, TimeSpan.Zero);

    /// <summary>
    /// A snapshot of per-worker utilization for every worker with at least one completed task
    /// (Req 15.6), keyed by <see cref="WorkerId"/>. The returned dictionary is an independent copy.
    /// </summary>
    public IReadOnlyDictionary<WorkerId, WorkerUtilization> PerWorkerUtilization() =>
        _perWorker.ToDictionary(
            kvp => kvp.Key,
            kvp => new WorkerUtilization(kvp.Value.LaborCost, kvp.Value.TasksCompleted, kvp.Value.BusyTime));
}
