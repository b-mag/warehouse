using Forge.Domain.Common;

namespace Forge.Domain.Spatial;

/// <summary>
/// The outcome of a path-planning request (Req 18.3, 18.6, 18.7). Either a
/// success carrying the ordered <see cref="Path"/> from origin to destination,
/// or an <see cref="IsUnroutable"/> result carrying the <see cref="DomainError"/>
/// with <see cref="ErrorKind.Unroutable"/>.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the codebase's result convention (see <see cref="Result{T}"/> and
/// <see cref="Fulfillment.FulfillmentResult"/>): an expected rejection (no path
/// exists, or an untraversable endpoint) is a plain value the caller inspects
/// rather than an exception. The Application handler that owns routing turns an
/// unroutable outcome into an <c>UnroutableTask</c> domain event (Req 18.6, task
/// 18.6) — this planner itself only reports the outcome.
/// </para>
/// </remarks>
public sealed class PathResult
{
    private PathResult(Path? path, DomainError? error)
    {
        _path = path;
        _error = error;
    }

    private readonly Path? _path;
    private readonly DomainError? _error;

    /// <summary>True when a traversable path was found.</summary>
    public bool IsSuccess => _path is not null;

    /// <summary>True when no traversable path exists between origin and destination.</summary>
    public bool IsUnroutable => _path is null;

    /// <summary>
    /// The planned path, ordered origin..destination. Throws if accessed on an
    /// unroutable result (a programming fault) — check <see cref="IsSuccess"/> first.
    /// </summary>
    public Path Path =>
        _path ?? throw new InvalidOperationException("Cannot access Path on an unroutable PathResult.");

    /// <summary>
    /// The <see cref="ErrorKind.Unroutable"/> error for an unroutable result. Throws
    /// if accessed on a success (a programming fault).
    /// </summary>
    public DomainError Error =>
        _error ?? throw new InvalidOperationException("Cannot access Error on a successful PathResult.");

    /// <summary>A successful result carrying the planned <paramref name="path"/>.</summary>
    public static PathResult Success(Path path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new PathResult(path, null);
    }

    /// <summary>
    /// An unroutable result — origin or destination is not traversable, or no path
    /// exists (Req 18.6). Carries a <see cref="DomainError"/> of
    /// <see cref="ErrorKind.Unroutable"/>.
    /// </summary>
    public static PathResult Unroutable(Cell origin, Cell destination) =>
        new(null, DomainError.Unroutable($"No traversable path from {origin} to {destination}.")
            .WithDetail("originX", origin.X)
            .WithDetail("originY", origin.Y)
            .WithDetail("destinationX", destination.X)
            .WithDetail("destinationY", destination.Y));
}

/// <summary>
/// A deterministic A* path planner over a <see cref="WarehouseGrid"/> (Req 18.3,
/// 18.7; design "Spatial Movement and Reservation Design &gt; Path planning";
/// Correctness Property 10). It is the domain-side planning <em>algorithm</em>;
/// the Application layer exposes it through its <c>IPathPlanner</c> abstraction
/// (task 17.1) and DI wires this concrete planner in.
/// </summary>
/// <remarks>
/// <para>
/// The planner is <b>pure</b> (no I/O, no mutable shared state) and
/// <b>deterministic</b>: an identical grid, origin, and destination always yield
/// an identical <see cref="Path"/> (Req 18.7). Determinism is guaranteed by two
/// choices, exactly as the design prescribes:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Fixed neighbor expansion order.</b> Neighbors are drawn from
/// <see cref="WarehouseGrid.Neighbors"/>, which yields traversable 4-connected
/// neighbors in the fixed order +X, −X, +Y, −Y.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A total ordering on the open set.</b> The frontier is a priority queue keyed
/// by <see cref="OpenKey"/>: primary key <c>f = g + h</c>, tie-break by lower
/// <c>h</c>, then by the cell in <c>(X, Y)</c> lexicographic order
/// (<see cref="Cell.CompareTo"/>). Because the cell coordinate is the final
/// discriminator and each cell appears in the frontier at most once conceptually,
/// no two distinct open entries ever compare equal, so the pop order is fully
/// determined by the key and never by hash-set/dictionary iteration order.
/// </description>
/// </item>
/// </list>
/// <para>
/// The heuristic is Manhattan distance, which is admissible and consistent on a
/// 4-connected uniform-cost grid, so the first time the destination is settled the
/// optimal cost has been found. Ties in optimal cost are resolved by the total
/// key above, giving one canonical shortest path.
/// </para>
/// </remarks>
public sealed class AStarPathPlanner
{
    /// <summary>
    /// The total ordering used for the A* open set (Req 18.7). Compared first by
    /// <see cref="F"/> (= g + h), then by lower <see cref="H"/>, then by
    /// <see cref="Cell"/> in <c>(X, Y)</c> lexicographic order. The trailing cell
    /// discriminator makes the ordering total: two entries are equal only when they
    /// refer to the same cell, so the frontier's pop order is deterministic.
    /// </summary>
    private readonly record struct OpenKey(int F, int H, Cell Cell) : IComparable<OpenKey>
    {
        public int CompareTo(OpenKey other)
        {
            int byF = F.CompareTo(other.F);
            if (byF != 0) return byF;

            int byH = H.CompareTo(other.H);
            if (byH != 0) return byH;

            return Cell.CompareTo(other.Cell);
        }
    }

    /// <summary>
    /// Plans a shortest path from <paramref name="origin"/> to
    /// <paramref name="destination"/> over the traversable, 4-connected cells of
    /// <paramref name="grid"/> (Req 18.3).
    /// </summary>
    /// <param name="grid">The warehouse grid. Must not be null.</param>
    /// <param name="origin">The starting cell.</param>
    /// <param name="destination">The goal cell.</param>
    /// <returns>
    /// <see cref="PathResult.Success"/> with the ordered origin..destination
    /// <see cref="Path"/> when a path exists (a single-cell path with zero steps
    /// when <paramref name="origin"/> equals <paramref name="destination"/>);
    /// otherwise <see cref="PathResult.Unroutable"/> when either endpoint is not
    /// traversable or no path connects them (Req 18.6).
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="grid"/> is null.</exception>
    public PathResult Plan(WarehouseGrid grid, Cell origin, Cell destination)
    {
        ArgumentNullException.ThrowIfNull(grid);

        // Untraversable endpoints can never be part of any path.
        if (!grid.IsTraversable(origin) || !grid.IsTraversable(destination))
        {
            return PathResult.Unroutable(origin, destination);
        }

        // Trivial path: already at the goal (zero steps).
        if (origin == destination)
        {
            return PathResult.Success(new Path(new[] { origin }));
        }

        // Best-known cost from origin to each settled/frontier cell, and the
        // predecessor used to reach it at that cost. Dictionary iteration order is
        // never consulted for planning decisions — every choice flows through the
        // totally-ordered priority queue below — so it cannot affect the result.
        var gScore = new Dictionary<Cell, int> { [origin] = 0 };
        var cameFrom = new Dictionary<Cell, Cell>();

        var open = new PriorityQueue<Cell, OpenKey>();
        open.Enqueue(origin, new OpenKey(F: Heuristic(origin, destination), H: Heuristic(origin, destination), Cell: origin));

        while (open.Count > 0)
        {
            Cell current = open.Dequeue();

            if (current == destination)
            {
                return PathResult.Success(Reconstruct(cameFrom, current));
            }

            int currentG = gScore[current];

            // Fixed expansion order (+X, −X, +Y, −Y) from the grid.
            foreach (Cell neighbor in grid.Neighbors(current))
            {
                int tentativeG = currentG + 1; // uniform step cost of 1 per move

                if (gScore.TryGetValue(neighbor, out int knownG) && tentativeG >= knownG)
                {
                    continue; // no improvement
                }

                cameFrom[neighbor] = current;
                gScore[neighbor] = tentativeG;

                int h = Heuristic(neighbor, destination);
                open.Enqueue(neighbor, new OpenKey(F: tentativeG + h, H: h, Cell: neighbor));
            }
        }

        return PathResult.Unroutable(origin, destination);
    }

    /// <summary>
    /// The Manhattan-distance heuristic between <paramref name="from"/> and
    /// <paramref name="to"/> — admissible and consistent for 4-connected,
    /// unit-cost movement (design "Path planning").
    /// </summary>
    private static int Heuristic(Cell from, Cell to) =>
        Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y);

    /// <summary>
    /// Walks the <paramref name="cameFrom"/> chain back from
    /// <paramref name="goal"/> to the origin and returns the cells ordered
    /// origin..goal.
    /// </summary>
    private static Path Reconstruct(IReadOnlyDictionary<Cell, Cell> cameFrom, Cell goal)
    {
        var reversed = new List<Cell> { goal };
        Cell node = goal;
        while (cameFrom.TryGetValue(node, out Cell parent))
        {
            reversed.Add(parent);
            node = parent;
        }

        reversed.Reverse();
        return new Path(reversed);
    }
}
