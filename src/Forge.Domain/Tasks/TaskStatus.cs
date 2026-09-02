namespace Forge.Domain.Tasks;

/// <summary>
/// The lifecycle state of a <see cref="WarehouseTask"/> (Req 8.2, 8.3, 8.4). A task starts
/// <see cref="Created"/> and moves through assignment and execution to <see cref="Completed"/>.
/// <para>
/// <b>Chosen value set and rationale.</b> The states are derived directly from the Req 8 flow:
/// a task is created (Req 8), assigned to an available worker (Req 8.2) or, when no worker is
/// available, queued until one becomes available (Req 8.3), then executed and completed (Req 8.4).
/// The values are:
/// <list type="bullet">
///   <item><description><see cref="Created"/> — initial state on construction, not yet assigned or queued.</description></item>
///   <item><description><see cref="Queued"/> — awaiting an available worker (Req 8.3 / 15.7 backlog).</description></item>
///   <item><description><see cref="Assigned"/> — bound to a worker but not yet started (Req 8.2).</description></item>
///   <item><description><see cref="InProgress"/> — the assigned worker is executing the task.</description></item>
///   <item><description><see cref="Completed"/> — terminal; completion recorded (Req 8.4).</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Allowed transitions</b> (enforced by <see cref="WarehouseTask"/>'s guarded state methods):
/// <c>Created → Queued</c> (Queue), <c>Created|Queued → Assigned</c> (AssignTo),
/// <c>Assigned → InProgress</c> (Start), and <c>InProgress → Completed</c> (Complete). Any other
/// transition is rejected, leaving the task's state unchanged. The Application task-assignment logic
/// (tasks 19.1 / 24) drives these transitions; the algorithm itself is not in the Domain.
/// </para>
/// </summary>
public enum TaskStatus
{
    /// <summary>Initial state: the task exists but is not yet queued or assigned.</summary>
    Created = 0,

    /// <summary>No worker was available, so the task waits in the backlog (Req 8.3, 15.7).</summary>
    Queued,

    /// <summary>Bound to a specific worker but not yet started (Req 8.2).</summary>
    Assigned,

    /// <summary>The assigned worker is actively executing the task.</summary>
    InProgress,

    /// <summary>Terminal state: the task is finished and completion has been recorded (Req 8.4).</summary>
    Completed,
}
