namespace Forge.Domain.Spatial;

/// <summary>
/// An ordered sequence of adjacent <see cref="WarehouseGrid"/> cells an
/// <see cref="Agent"/> traverses from an origin cell to a destination cell
/// (Req 18.1). Produced by the A* planner (task 14.2).
/// </summary>
/// <remarks>
/// A path of <c>n</c> cells has <c>n - 1</c> steps (segments). A single-cell path
/// (origin == destination) has no steps and zero traversal time. An empty
/// <see cref="Cells"/> list is treated as an empty (zero-step) path.
/// </remarks>
public sealed record Path(IReadOnlyList<Cell> Cells)
{
    /// <summary>
    /// The number of steps (segments) in the path: <c>max(0, Cells.Count - 1)</c>.
    /// This is the distance the agent travels, in cells, and feeds
    /// <see cref="TraversalTime"/> (Req 18.5).
    /// </summary>
    public int StepCount => Cells.Count > 0 ? Cells.Count - 1 : 0;

    /// <summary>
    /// The path's steps as pairwise-consecutive <see cref="PathSegment"/>s. Yields
    /// nothing for empty or single-cell paths. The reservation ledger (task 15)
    /// reserves each segment for the interval the agent occupies it.
    /// </summary>
    public IEnumerable<PathSegment> Segments
    {
        get
        {
            for (int i = 0; i + 1 < Cells.Count; i++)
            {
                yield return new PathSegment(Cells[i], Cells[i + 1]);
            }
        }
    }

    /// <summary>
    /// The simulated time to traverse the whole path at <paramref name="cellsPerSecond"/>,
    /// derived as <c>StepCount / cellsPerSecond</c> (Req 15.4, 18.5). This feeds a
    /// Warehouse_Task's Travel_Time.
    /// </summary>
    /// <param name="cellsPerSecond">The agent's movement speed in cells per second.</param>
    /// <returns>
    /// <see cref="TimeSpan.Zero"/> when the path has no steps. For a path with steps,
    /// a non-positive or non-finite <paramref name="cellsPerSecond"/> has no meaningful
    /// travel time, so this returns <see cref="TimeSpan.MaxValue"/> (a stationary agent
    /// never arrives) rather than throwing or producing a negative/NaN duration. This
    /// keeps the method total and deterministic for the planner and callers.
    /// </returns>
    public TimeSpan TraversalTime(double cellsPerSecond)
    {
        int steps = StepCount;
        if (steps == 0)
        {
            return TimeSpan.Zero;
        }

        if (!double.IsFinite(cellsPerSecond) || cellsPerSecond <= 0)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromSeconds(steps / cellsPerSecond);
    }
}
