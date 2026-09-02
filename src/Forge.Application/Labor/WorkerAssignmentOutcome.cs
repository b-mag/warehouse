using Forge.Domain.Common;

namespace Forge.Application.Labor;

/// <summary>
/// The result of attempting to assign a <c>WarehouseTask</c> to an on-shift worker
/// (Req 8.2, 8.3, 15.5, 15.7).
/// <para>
/// There are exactly two shift-gated outcomes and the service reports them as a value the caller
/// inspects rather than throwing:
/// <list type="bullet">
///   <item><description><see cref="Assigned"/> — a worker on shift at the current time was selected
///   and the task was bound to that worker (Req 8.2, 15.5).</description></item>
///   <item><description><see cref="NoWorkerAvailable"/> — no worker was on shift, so the task is left
///   queued and joins the backlog (Req 8.3, 15.7). Backlog <em>counting</em> itself is task 22.1; this
///   outcome is the signal the backlog logic consumes.</description></item>
/// </list>
/// A distinct <see cref="Rejected"/> case carries a domain error for the rare case where the domain
/// task refuses the state transition (e.g. the task is not in an assignable status), so a genuine
/// rejection is never confused with "no worker on shift".
/// </para>
/// </summary>
public readonly record struct WorkerAssignmentOutcome
{
    private WorkerAssignmentOutcome(
        WorkerAssignmentStatus status,
        WorkerId? worker,
        DomainError? error)
    {
        Status = status;
        Worker = worker;
        _error = error;
    }

    private readonly DomainError? _error;

    /// <summary>Which of the shift-gated outcomes occurred.</summary>
    public WorkerAssignmentStatus Status { get; }

    /// <summary>The selected worker on <see cref="WorkerAssignmentStatus.Assigned"/>; otherwise <c>null</c>.</summary>
    public WorkerId? Worker { get; }

    /// <summary>True when a worker on shift was selected and the task was assigned (Req 8.2, 15.5).</summary>
    public bool IsAssigned => Status == WorkerAssignmentStatus.Assigned;

    /// <summary>True when no worker was on shift; the task stays queued and joins the backlog (Req 8.3, 15.7).</summary>
    public bool IsNoWorkerAvailable => Status == WorkerAssignmentStatus.NoWorkerAvailable;

    /// <summary>True when the domain refused the assignment transition (a typed rejection).</summary>
    public bool IsRejected => Status == WorkerAssignmentStatus.Rejected;

    /// <summary>The rejection error. Throws if accessed on a non-rejected outcome.</summary>
    public DomainError Error =>
        _error ?? throw new InvalidOperationException("Cannot access Error on a non-rejected assignment outcome.");

    /// <summary>A task assigned to the given on-shift <paramref name="worker"/> (Req 8.2, 15.5).</summary>
    public static WorkerAssignmentOutcome Assigned(WorkerId worker) =>
        new(WorkerAssignmentStatus.Assigned, worker, null);

    /// <summary>No worker on shift: the task is left queued and added to the backlog (Req 8.3, 15.7).</summary>
    public static WorkerAssignmentOutcome NoWorkerAvailable() =>
        new(WorkerAssignmentStatus.NoWorkerAvailable, null, null);

    /// <summary>The domain refused the assignment transition, carrying the typed <paramref name="error"/>.</summary>
    public static WorkerAssignmentOutcome Rejected(DomainError error) =>
        new(WorkerAssignmentStatus.Rejected, null, error);
}

/// <summary>The three mutually-exclusive results of a shift-gated assignment attempt.</summary>
public enum WorkerAssignmentStatus
{
    /// <summary>A worker on shift was selected and the task was assigned (Req 8.2, 15.5).</summary>
    Assigned = 0,

    /// <summary>No worker was on shift; the task stays queued and joins the backlog (Req 8.3, 15.7).</summary>
    NoWorkerAvailable,

    /// <summary>The domain refused the state transition (e.g. task not in an assignable status).</summary>
    Rejected,
}
