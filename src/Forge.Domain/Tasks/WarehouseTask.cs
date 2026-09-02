using Forge.Domain.Common;
using Forge.Domain.Spatial;

namespace Forge.Domain.Tasks;

/// <summary>
/// A unit of warehouse work of a defined <see cref="WarehouseTaskType"/> moving between two grid
/// cells (Req 8.1). A task carries an estimated duration guaranteed <c>&gt;= 0</c> and, once a path is
/// planned, a travel time also guaranteed <c>&gt;= 0</c>. Construction goes through the validated
/// <see cref="Create"/> factory so a task with a negative duration can never exist (Req 15.2, 15.8).
/// <para>
/// <b>Scope.</b> This type models the task data plus minimal guarded lifecycle transitions
/// (<see cref="Queue"/>, <see cref="AssignTo(WorkerId)"/>, <see cref="Start"/>, <see cref="Complete"/>)
/// and a guarded <see cref="SetTravelTime(TimeSpan)"/> setter. It deliberately does <b>not</b> implement
/// the worker-assignment algorithm, labor-cost accrual, per-worker utilization, or backlog logic — those
/// are Application concerns (tasks 19.1 / 24.2 / 24.5). In particular, <c>LaborCost = (EstimatedDuration
/// + TravelTime) × HourlyRate</c> is accrued by the Application on completion (task 19.1) and is not
/// computed here; this type only models the data and its validation.
/// </para>
/// <para>
/// All rejections leave the task's state unchanged (the repeated Domain invariant), returning a typed
/// <see cref="Result"/> rather than throwing for expected rejections.
/// </para>
/// </summary>
public sealed class WarehouseTask
{
    private WarehouseTask(
        WarehouseTaskId id,
        WarehouseTaskType type,
        Cell origin,
        Cell destination,
        TimeSpan estimatedDuration)
    {
        Id = id;
        Type = type;
        Origin = origin;
        Destination = destination;
        EstimatedDuration = estimatedDuration;
        TravelTime = TimeSpan.Zero;
        AssignedWorker = null;
        Status = TaskStatus.Created;
    }

    /// <summary>The task's stable identity (Req 3.1).</summary>
    public WarehouseTaskId Id { get; }

    /// <summary>The kind of work this task represents (Req 8.1).</summary>
    public WarehouseTaskType Type { get; }

    /// <summary>The grid cell the work starts from.</summary>
    public Cell Origin { get; }

    /// <summary>The grid cell the work ends at.</summary>
    public Cell Destination { get; }

    /// <summary>Estimated work duration, guaranteed <c>&gt;= 0</c> (Req 15.2, negative rejected Req 15.8).</summary>
    public TimeSpan EstimatedDuration { get; }

    /// <summary>
    /// Travel time derived from the assigned agent's planned path traversal, guaranteed <c>&gt;= 0</c>
    /// (Req 15.4, 18.5). Defaults to <see cref="TimeSpan.Zero"/> and is set later via
    /// <see cref="SetTravelTime(TimeSpan)"/> once a path is planned.
    /// </summary>
    public TimeSpan TravelTime { get; private set; }

    /// <summary>The worker this task is assigned to, or <c>null</c> while unassigned (Req 8.2).</summary>
    public WorkerId? AssignedWorker { get; private set; }

    /// <summary>The current lifecycle state (Req 8.2, 8.3, 8.4). See <see cref="TaskStatus"/> for transitions.</summary>
    public TaskStatus Status { get; private set; }

    /// <summary>
    /// Validated factory returning a <see cref="WarehouseTask"/> on success or a typed error on rejection
    /// (Req 15.2, 15.8). Rejects a negative <paramref name="estimatedDuration"/> with
    /// <see cref="DomainError.InvalidValue(string)"/>, leaving no task constructed. A new task starts with
    /// <see cref="TravelTime"/> zero, no <see cref="AssignedWorker"/>, and status
    /// <see cref="TaskStatus.Created"/>.
    /// </summary>
    /// <param name="id">The task's identity.</param>
    /// <param name="type">The kind of work (Req 8.1).</param>
    /// <param name="origin">The starting grid cell.</param>
    /// <param name="destination">The ending grid cell.</param>
    /// <param name="estimatedDuration">Estimated work duration; must be <c>&gt;= 0</c> (Req 15.2, 15.8).</param>
    /// <returns>A successful <see cref="Result{WarehouseTask}"/> when valid, otherwise a typed rejection.</returns>
    public static Result<WarehouseTask> Create(
        WarehouseTaskId id,
        WarehouseTaskType type,
        Cell origin,
        Cell destination,
        TimeSpan estimatedDuration)
    {
        if (estimatedDuration < TimeSpan.Zero)
        {
            return DomainError.InvalidValue(
                $"Warehouse task estimated duration must be greater than or equal to zero; got {estimatedDuration}.");
        }

        return new WarehouseTask(id, type, origin, destination, estimatedDuration);
    }

    /// <summary>
    /// Sets the task's <see cref="TravelTime"/> from a planned path traversal (Req 15.4, 18.5). Rejects a
    /// negative <paramref name="travelTime"/> with <see cref="DomainError.InvalidValue(string)"/>, leaving
    /// <see cref="TravelTime"/> unchanged. The Application (task 19.1) supplies the value derived from
    /// <c>Path.TraversalTime</c>; this guard keeps the non-negative invariant regardless of caller.
    /// </summary>
    /// <param name="travelTime">The planned traversal time; must be <c>&gt;= 0</c>.</param>
    /// <returns>A successful <see cref="Result"/> when applied, otherwise a typed rejection.</returns>
    public Result SetTravelTime(TimeSpan travelTime)
    {
        if (travelTime < TimeSpan.Zero)
        {
            return DomainError.InvalidValue(
                $"Warehouse task travel time must be greater than or equal to zero; got {travelTime}.");
        }

        TravelTime = travelTime;
        return Result.Success();
    }

    /// <summary>
    /// Moves the task into the <see cref="TaskStatus.Queued"/> backlog state (Req 8.3, 15.7). Permitted only
    /// from <see cref="TaskStatus.Created"/>; any other current status is rejected leaving state unchanged.
    /// The decision of <em>when</em> to queue (no worker on shift) belongs to the Application (tasks 19.1 / 24).
    /// </summary>
    /// <returns>A successful <see cref="Result"/> when queued, otherwise a typed rejection.</returns>
    public Result Queue()
    {
        if (Status != TaskStatus.Created)
        {
            return DomainError.Validation(
                $"A warehouse task can only be queued from the Created state; current status is {Status}.");
        }

        Status = TaskStatus.Queued;
        return Result.Success();
    }

    /// <summary>
    /// Assigns the task to <paramref name="worker"/> and moves it to <see cref="TaskStatus.Assigned"/>
    /// (Req 8.2). Permitted only from <see cref="TaskStatus.Created"/> or <see cref="TaskStatus.Queued"/>;
    /// any other current status is rejected leaving <see cref="AssignedWorker"/> and <see cref="Status"/>
    /// unchanged. The shift-gated selection of <em>which</em> worker is an Application concern (task 19.1).
    /// </summary>
    /// <param name="worker">The worker to assign the task to.</param>
    /// <returns>A successful <see cref="Result"/> when assigned, otherwise a typed rejection.</returns>
    public Result AssignTo(WorkerId worker)
    {
        if (Status is not (TaskStatus.Created or TaskStatus.Queued))
        {
            return DomainError.Validation(
                $"A warehouse task can only be assigned from the Created or Queued state; current status is {Status}.");
        }

        AssignedWorker = worker;
        Status = TaskStatus.Assigned;
        return Result.Success();
    }

    /// <summary>
    /// Marks an assigned task as <see cref="TaskStatus.InProgress"/>. Permitted only from
    /// <see cref="TaskStatus.Assigned"/>; any other current status is rejected leaving state unchanged.
    /// </summary>
    /// <returns>A successful <see cref="Result"/> when started, otherwise a typed rejection.</returns>
    public Result Start()
    {
        if (Status != TaskStatus.Assigned)
        {
            return DomainError.Validation(
                $"A warehouse task can only be started from the Assigned state; current status is {Status}.");
        }

        Status = TaskStatus.InProgress;
        return Result.Success();
    }

    /// <summary>
    /// Marks an in-progress task as <see cref="TaskStatus.Completed"/> (Req 8.4). Permitted only from
    /// <see cref="TaskStatus.InProgress"/>; any other current status is rejected leaving state unchanged.
    /// Recording the completion event and accruing labor cost are Application concerns (tasks 24.5 / 19.1);
    /// this method only advances the lifecycle state.
    /// </summary>
    /// <returns>A successful <see cref="Result"/> when completed, otherwise a typed rejection.</returns>
    public Result Complete()
    {
        if (Status != TaskStatus.InProgress)
        {
            return DomainError.Validation(
                $"A warehouse task can only be completed from the InProgress state; current status is {Status}.");
        }

        Status = TaskStatus.Completed;
        return Result.Success();
    }
}
