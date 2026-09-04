using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Gels;

namespace Forge.Application.Simulation;

/// <summary>
/// The result of the task-execution stage (<see cref="TickStages.TaskExecution"/>): the completion
/// events to publish after the atomic commit, the per-type processed counts the metrics stage folds into
/// throughput/backlog, plus temporary diagnostic counters explaining assignment outcomes this tick.
/// </summary>
internal sealed record TaskExecutionOutcome(
    IReadOnlyList<IDomainEvent> Events,
    int PutAwayCompleted,
    int PickCompleted,
    int Assigned,
    int SkippedUnroutable,
    int SkippedAssignFailed,
    int InFlightNotArrived,
    int QueueDepth,
    IReadOnlyList<GelLotId> CompletedPutAwayLotIds,
    /// <summary>Lots pulled from a holding zone onto a worker (zone.TryRemove).</summary>
    IReadOnlyList<GelLotId> PickedUpLotIds);