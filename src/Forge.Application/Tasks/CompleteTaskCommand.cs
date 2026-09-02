using Forge.Domain.Common;

namespace Forge.Application.Tasks;

/// <summary>
/// Command to complete a warehouse task (Req 8.4, 8.5, 15.3). Handled by
/// <see cref="CompleteTaskHandler"/>: it loads the identified <c>WarehouseTask</c>, drives the guarded
/// domain <c>Complete()</c> transition (which rejects unless the task is in progress), accrues the task's
/// labor cost to the assigned worker, publishes a <c>TaskCompleted</c> domain event, and persists the
/// change. Carries only the task's identity — everything else (duration, travel time, worker, rate) is
/// loaded from the persisted task and worker so the caller cannot supply inconsistent values.
/// </summary>
/// <param name="TaskId">The identity of the task to complete.</param>
public sealed record CompleteTaskCommand(WarehouseTaskId TaskId);
