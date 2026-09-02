using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Repositories;
using Forge.Application.Labor;
using Forge.Domain.Common;
using Forge.Domain.Events;

namespace Forge.Application.Tasks;

/// <summary>
/// Completes a warehouse task: records the completion, accrues its labor cost, publishes a completion
/// domain event, and persists the change (Req 8.4, 8.5, 15.3). This is the CQRS-style use-case handler
/// for task completion (task 24.5), invoked by the tick pipeline / gateway when a worker finishes a task.
/// <para>
/// <b>Flow.</b> Handling <see cref="CompleteTaskCommand"/>:
/// <list type="number">
///   <item><description>Load the <c>WarehouseTask</c> by id; reject an unknown id with
///   <see cref="ErrorKind.Validation"/> (no state changed).</description></item>
///   <item><description>Drive the guarded domain <c>WarehouseTask.Complete()</c> transition. It rejects
///   unless the task is <c>InProgress</c>, leaving the task's state unchanged; that rejection is returned
///   verbatim so nothing is accrued, published, or persisted (Req 8.4).</description></item>
///   <item><description>Accrue the task's labor cost via <see cref="LaborLedger.AccrueOnCompletion"/> using
///   the task's <c>EstimatedDuration</c> + <c>TravelTime</c> and the assigned worker's <c>HourlyRate</c>,
///   loaded via <see cref="IWorkerRepository"/> (Req 15.3). An assigned task without a resolvable worker is
///   rejected before the transition is committed anywhere durable.</description></item>
///   <item><description>Record the completion by staging the task update and publishing a
///   <see cref="TaskCompleted"/> event through <see cref="IEventBus"/> (Req 8.4, 8.5).</description></item>
///   <item><description>Persist atomically via <see cref="IUnitOfWork"/>.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Layering.</b> The handler depends only on Application abstractions (repositories, unit of work,
/// event bus, clock) and the pure domain — never on concrete infrastructure — keeping the WMS Core's
/// Domain+Contracts-only reference boundary intact.
/// </para>
/// <para>
/// <b>The <see cref="LaborLedger"/> is a stateful, singleton-style Application component.</b> It accumulates
/// total labor cost and per-worker utilization <em>across</em> completions, so the same instance must be
/// shared by every <see cref="CompleteTaskHandler"/> invocation for the running warehouse (registered as a
/// singleton by the composition root). It is injected here rather than constructed per call precisely so the
/// running total survives between completions; a fresh ledger per handler would reset the totals each time.
/// </para>
/// <para>
/// <b>Deterministic accrual.</b> The accrued amount is <c>(EstimatedDuration + TravelTime) × HourlyRate</c>
/// computed exactly in <see cref="decimal"/> by the ledger, so two completions with identical duration,
/// travel time, and rate accrue an identical cost (Req 15.3). The completion is timestamped from
/// <see cref="IClock.Now"/> so the event's time is the current simulated time, not wall time.
/// </para>
/// </summary>
public sealed class CompleteTaskHandler
{
    private readonly ITaskRepository _tasks;
    private readonly IWorkerRepository _workers;
    private readonly LaborLedger _ledger;
    private readonly IEventBus _eventBus;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    /// <summary>
    /// Construct the handler from the Application abstractions it drives. The <paramref name="ledger"/> is a
    /// shared, singleton-style component so accrual accumulates across every completion (see the type remarks).
    /// </summary>
    /// <param name="tasks">Task repository used to load and stage the completed task.</param>
    /// <param name="workers">Worker repository used to resolve the assigned worker's hourly rate (Req 15.3).</param>
    /// <param name="ledger">The shared labor ledger accruing cost + utilization across completions (Req 15.3, 15.6).</param>
    /// <param name="eventBus">The bus the completion event is published through (Req 8.4, 8.5).</param>
    /// <param name="unitOfWork">The transactional boundary changes are committed through.</param>
    /// <param name="clock">The clock supplying the completion timestamp (current simulated time).</param>
    public CompleteTaskHandler(
        ITaskRepository tasks,
        IWorkerRepository workers,
        LaborLedger ledger,
        IEventBus eventBus,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Complete the task named by <paramref name="command"/> (Req 8.4, 8.5, 15.3). Returns a successful
    /// <see cref="Result"/> when the task was completed, accrued, published, and persisted; otherwise a typed
    /// rejection that leaves all state unchanged (unknown id, task not in progress, or an assigned task whose
    /// worker cannot be resolved). See the type remarks for the full flow.
    /// </summary>
    /// <param name="command">The completion command carrying the task id.</param>
    /// <param name="ct">A cancellation token for the async persistence/publish operations.</param>
    /// <returns>A task yielding the completion outcome.</returns>
    public async Task<Result> HandleAsync(CompleteTaskCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var task = await _tasks.GetByIdAsync(command.TaskId, ct).ConfigureAwait(false);
        if (task is null)
        {
            return DomainError.Validation(
                $"No warehouse task exists with id {command.TaskId}.", nameof(command.TaskId));
        }

        // A task reaching completion must have been assigned to a worker (Created|Queued -> Assigned ->
        // InProgress -> Completed). Resolve the worker BEFORE the transition so a missing worker rejects
        // without mutating or persisting anything (Req 15.3 needs the rate to accrue).
        if (task.AssignedWorker is not { } workerId)
        {
            return DomainError.Validation(
                $"Warehouse task {command.TaskId} cannot be completed because it has no assigned worker.");
        }

        var worker = await _workers.GetByIdAsync(workerId, ct).ConfigureAwait(false);
        if (worker is null)
        {
            return DomainError.Validation(
                $"Assigned worker {workerId} for task {command.TaskId} could not be found.");
        }

        // Guarded domain transition: rejects unless InProgress, leaving state unchanged (Req 8.4).
        var completion = task.Complete();
        if (completion.IsFailure)
        {
            return completion;
        }

        // Accrue labor cost for the completed task to the shared ledger (Req 15.3). Cost is
        // (EstimatedDuration + TravelTime) x HourlyRate, computed exactly in decimal.
        decimal laborCost = _ledger.AccrueOnCompletion(
            workerId, task.EstimatedDuration, task.TravelTime, worker.HourlyRate);

        // Record the completion (stage the status change) and publish the completion event (Req 8.4, 8.5).
        _tasks.Update(task);

        var completedEvent = new TaskCompleted(task.Id, workerId, laborCost, _clock.Now);
        await _eventBus.PublishAsync(completedEvent, ct).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }
}
