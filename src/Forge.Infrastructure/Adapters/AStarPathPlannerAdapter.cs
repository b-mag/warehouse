using Forge.Application.Abstractions;
using Forge.Domain.Spatial;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// The composition-root adapter that exposes the domain <see cref="AStarPathPlanner"/> behind the
/// Application <see cref="IPathPlanner"/> seam (task 33.3; Req 18.3, 18.7). The planning algorithm and
/// its result type live in <c>Forge.Domain.Spatial</c>; this adapter only forwards, so the core's
/// per-tick movement stage can plan paths without referencing the concrete domain planner directly.
/// <para>
/// The planner is stateless and deterministic (an identical grid + origin + destination always yields
/// an identical path — Req 18.7), so a single shared instance is safe to register as a singleton.
/// </para>
/// </summary>
public sealed class AStarPathPlannerAdapter : IPathPlanner
{
    private readonly AStarPathPlanner _planner = new();

    /// <inheritdoc />
    public PathResult Plan(WarehouseGrid grid, Cell origin, Cell destination) =>
        _planner.Plan(grid, origin, destination);
}
