using Forge.Domain.Spatial;

namespace Forge.Application.Abstractions;

/// <summary>
/// The deterministic path-planning seam (design "WMS Core Application abstractions"; Req 18.3).
/// The algorithm and result type (<see cref="AStarPathPlanner"/>, <see cref="PathResult"/>) live in
/// <c>Forge.Domain.Spatial</c>; this abstraction lets DI expose the domain planner to the core's
/// handlers. A plan is deterministic given an identical grid, origin, and destination (Req 18.7).
/// </summary>
public interface IPathPlanner
{
    /// <summary>
    /// Plan a shortest path from <paramref name="origin"/> to <paramref name="destination"/> over
    /// the traversable cells of <paramref name="grid"/>, or an unroutable result when none exists
    /// (Req 18.3, 18.6).
    /// </summary>
    PathResult Plan(WarehouseGrid grid, Cell origin, Cell destination);
}
