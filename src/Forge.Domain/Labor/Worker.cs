using Forge.Domain.Common;

namespace Forge.Domain.Labor;

/// <summary>
/// A labor worker with an hourly rate and one or more shifts (Req 15.1). The rate is guaranteed
/// <c>&gt;= 0</c> and the worker always has at least one <see cref="WorkerShift"/>, because construction
/// goes through the validated <see cref="Create(WorkerId, decimal, IReadOnlyList{WorkerShift})"/> factory
/// so an invalid worker can never exist.
/// <para>
/// This type models the worker plus the pure <see cref="IsOnShift(DateTimeOffset)"/> predicate (Req 15.5).
/// Labor-cost accrual (<c>LaborCost = (duration + travel) × rate</c>), per-worker utilization, and the
/// shift-gated assignment flow are Application concerns (task 19.1) that consume this model; they are not
/// implemented here.
/// </para>
/// </summary>
public sealed class Worker
{
    private Worker(WorkerId id, decimal hourlyRate, IReadOnlyList<WorkerShift> shifts)
    {
        Id = id;
        HourlyRate = hourlyRate;
        Shifts = shifts;
    }

    /// <summary>The worker's stable identity (Req 3.1).</summary>
    public WorkerId Id { get; }

    /// <summary>The worker's hourly pay rate, guaranteed <c>&gt;= 0</c> (Req 15.1, negative rejected Req 15.8).</summary>
    public decimal HourlyRate { get; }

    /// <summary>The worker's shifts; guaranteed to contain at least one shift (Req 15.1).</summary>
    public IReadOnlyList<WorkerShift> Shifts { get; }

    /// <summary>
    /// Validated factory returning a <see cref="Worker"/> on success or a typed error on rejection
    /// (Req 15.1, 15.8). Rejects a negative <paramref name="hourlyRate"/> with
    /// <see cref="DomainError.InvalidValue(string)"/> (Req 15.8) and rejects a null or empty
    /// <paramref name="shifts"/> collection (Req 15.1 requires one or more shifts), leaving no worker
    /// constructed. The shifts are copied into an independent list so the worker's schedule cannot be
    /// mutated through the caller's reference.
    /// </summary>
    /// <param name="id">The worker's identity.</param>
    /// <param name="hourlyRate">The hourly pay rate; must be <c>&gt;= 0</c> (Req 15.8).</param>
    /// <param name="shifts">One or more validated shifts; must be non-empty (Req 15.1).</param>
    /// <returns>A successful <see cref="Result{Worker}"/> when valid, otherwise a typed rejection.</returns>
    public static Result<Worker> Create(
        WorkerId id,
        decimal hourlyRate,
        IReadOnlyList<WorkerShift> shifts)
    {
        if (hourlyRate < 0m)
        {
            return DomainError.InvalidValue(
                $"Worker hourly rate must be greater than or equal to zero; got {hourlyRate}.");
        }

        if (shifts is null || shifts.Count == 0)
        {
            return DomainError.InvalidValue("Worker must have at least one shift.");
        }

        return new Worker(id, hourlyRate, shifts.ToArray());
    }

    /// <summary>
    /// Pure predicate: returns <c>true</c> when <paramref name="now"/> falls within any of the worker's
    /// shifts, using each shift's inclusive bounds (Req 15.5). Deterministic and side-effect free. This is
    /// the domain-side gate the Application's assignment logic (task 19.1) uses to decide whether a worker
    /// may take a task at the current simulated time.
    /// </summary>
    /// <param name="now">The moment to test against the worker's shifts.</param>
    /// <returns><c>true</c> if <paramref name="now"/> is inside at least one shift.</returns>
    public bool IsOnShift(DateTimeOffset now)
    {
        foreach (var shift in Shifts)
        {
            if (shift.Contains(now))
            {
                return true;
            }
        }

        return false;
    }
}
