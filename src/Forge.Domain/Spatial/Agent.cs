using Forge.Domain.Common;

namespace Forge.Domain.Spatial;

/// <summary>
/// A moving simulation entity (a picker or forklift) that occupies exactly one
/// <see cref="WarehouseGrid"/> cell at any instant of simulated time and moves
/// along a planned <see cref="Path"/> (Req 18.1, 18.2). An Agent is the spatial
/// representation of a Worker executing Warehouse_Tasks; its <see cref="Id"/> is
/// associated with a <see cref="WorkerId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The Agent holds the minimal spatial state the movement rule needs. The actual
/// per-tick movement application — advancing along the reserved path by
/// <c>speed × delta</c>, acquiring/releasing reservations, re-planning on conflict —
/// is core Application logic (tasks 24.4 / 27.6). This type deliberately exposes
/// only the small, guarded mutations that logic drives: assign a path and update
/// the occupied cell.
/// </para>
/// <para>
/// The single-cell invariant (Req 18.2) is structural: <see cref="Position"/> is a
/// single <see cref="Cell"/>, so an agent can only ever be at one cell.
/// </para>
/// </remarks>
public sealed class Agent
{
    /// <summary>The agent's identity, associated with a <see cref="WorkerId"/>.</summary>
    public AgentId Id { get; }

    /// <summary>The Worker this agent represents while executing tasks.</summary>
    public WorkerId Worker { get; }

    /// <summary>The single cell the agent currently occupies (Req 18.2).</summary>
    public Cell Position { get; private set; }

    /// <summary>The agent's movement speed in cells per second (Req 18.4).</summary>
    public double CellsPerSecond { get; }

    /// <summary>
    /// The path the agent is currently following, or <c>null</c> when it has no
    /// assignment. Set via <see cref="AssignPath"/> and cleared via
    /// <see cref="ClearPath"/>.
    /// </summary>
    public Path? CurrentPath { get; private set; }

    /// <summary>
    /// Creates an agent at <paramref name="startPosition"/> with the given movement speed.
    /// </summary>
    /// <param name="cellsPerSecond">Movement speed in cells per second; must be positive and finite (Req 18.4).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="cellsPerSecond"/> is not a positive, finite value.
    /// </exception>
    public Agent(AgentId id, WorkerId worker, Cell startPosition, double cellsPerSecond)
    {
        if (!double.IsFinite(cellsPerSecond) || cellsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellsPerSecond),
                cellsPerSecond,
                "Movement speed must be a positive, finite number of cells per second.");
        }

        Id = id;
        Worker = worker;
        Position = startPosition;
        CellsPerSecond = cellsPerSecond;
    }

    /// <summary>
    /// Assigns a path for the agent to follow. The movement rule (tasks 24.4 / 27.6)
    /// consumes this to advance the agent over simulated time.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> is null.</exception>
    public void AssignPath(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        CurrentPath = path;
    }

    /// <summary>Clears the current path assignment (e.g., on task completion or re-plan).</summary>
    public void ClearPath() => CurrentPath = null;

    /// <summary>
    /// Moves the agent to <paramref name="cell"/>. Called by the movement rule as the
    /// agent advances along its path, preserving the single-cell-occupancy invariant
    /// (Req 18.2). Kept minimal here; the rule owns when and how far the agent moves.
    /// </summary>
    public void MoveTo(Cell cell) => Position = cell;
}
