using CsCheck;

using Forge.Domain.Spatial;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 10: Path-planning determinism
//
// Property 10 (design.md): "For any grid state and any origin and destination cells, computing a
// path twice SHALL produce an identical path." Determinism is guaranteed by the planner's fixed
// neighbor expansion order (+X, −X, +Y, −Y) and its total open-set ordering (f, then lower h, then
// cell in (X, Y) lexicographic order), so no hash-set iteration order can influence the result.
//
// Alongside determinism this exercises path validity: a successful path starts at the origin, ends
// at the destination, visits only traversable cells, and moves between 4-adjacent cells only; and a
// trivial origin==destination request yields a single-cell (zero-step) path.
//
// Validates: Requirements 18.7, 28.10
public sealed class PathPlanningProperties
{
    // ≥100 iterations required by the spec; set explicitly on every Sample(..., iter: Iterations).
    private const int Iterations = 100;

    /// <summary>
    /// A randomly generated planning scenario: a grid extent, the set of blocked (obstacle) cells,
    /// and an origin/destination pair. Coordinates for origin/destination are kept within the grid
    /// extent so the endpoints are meaningful; whether they are traversable depends on the blocked
    /// set, which lets both routable and unroutable cases arise.
    /// </summary>
    private sealed record Scenario(
        int Width,
        int Height,
        IReadOnlyList<Cell> Blocked,
        Cell Origin,
        Cell Destination);

    // Small bounds keep grids dense enough that many random origin/destination pairs are routable
    // while still producing plenty of obstacle-driven unroutable cases.
    private static readonly Gen<Scenario> GenScenario =
        from width in Gen.Int[1, 12]
        from height in Gen.Int[1, 12]
        // Blocked cells: a random-length subset of in-bounds cells. Length is bounded by the grid
        // area so we never ask for more distinct cells than exist.
        from blockedCount in Gen.Int[0, width * height]
        from blocked in GenCell(width, height).Array[0, blockedCount]
        from origin in GenCell(width, height)
        from destination in GenCell(width, height)
        select new Scenario(width, height, blocked, origin, destination);

    private static Gen<Cell> GenCell(int width, int height) =>
        from x in Gen.Int[0, width - 1]
        from y in Gen.Int[0, height - 1]
        select new Cell(x, y);

    private static WarehouseGrid BuildGrid(Scenario s) =>
        new(s.Width, s.Height, s.Blocked);

    // Req 18.7 / 28.10 / Property 10: planning the SAME (grid, origin, destination) twice yields an
    // identical PathResult — same success flag, and when successful the same ordered cell sequence.
    [Fact]
    public void PlanningTwice_YieldsIdenticalResult()
    {
        GenScenario.Sample(s =>
        {
            var planner = new AStarPathPlanner();
            var grid = BuildGrid(s);

            var first = planner.Plan(grid, s.Origin, s.Destination);
            var second = planner.Plan(grid, s.Origin, s.Destination);

            if (first.IsSuccess != second.IsSuccess)
            {
                return false;
            }

            if (!first.IsSuccess)
            {
                // Both unroutable — determinism satisfied.
                return true;
            }

            // Both successful: the ordered cell sequences must be identical.
            return first.Path.Cells.SequenceEqual(second.Path.Cells);
        }, iter: Iterations);
    }

    // Req 18.7 / Property 10: when a path is found it must be a valid path — it starts at origin,
    // ends at destination, every cell is traversable, and consecutive cells are 4-adjacent.
    [Fact]
    public void SuccessfulPath_IsValid()
    {
        GenScenario.Sample(s =>
        {
            var planner = new AStarPathPlanner();
            var grid = BuildGrid(s);

            var result = planner.Plan(grid, s.Origin, s.Destination);
            if (!result.IsSuccess)
            {
                return true; // validity is only asserted on successful results
            }

            var cells = result.Path.Cells;

            if (cells.Count == 0)
            {
                return false; // a successful path always contains at least the origin cell
            }

            if (cells[0] != s.Origin || cells[^1] != s.Destination)
            {
                return false;
            }

            for (int i = 0; i < cells.Count; i++)
            {
                if (!grid.IsTraversable(cells[i]))
                {
                    return false;
                }

                if (i > 0 && !AreFourAdjacent(cells[i - 1], cells[i]))
                {
                    return false;
                }
            }

            return true;
        }, iter: Iterations);
    }

    // Req 18.7 / Property 10: a traversable origin == destination request yields a single-cell path
    // with zero steps (the trivial "already there" path).
    [Fact]
    public void TraversableOriginEqualsDestination_YieldsSingleCellZeroStepPath()
    {
        GenScenario.Sample(s =>
        {
            var planner = new AStarPathPlanner();
            var grid = BuildGrid(s);

            // Only meaningful when the shared endpoint is traversable; otherwise the request is
            // legitimately unroutable and covered elsewhere.
            if (!grid.IsTraversable(s.Origin))
            {
                return true;
            }

            var result = planner.Plan(grid, s.Origin, s.Origin);

            return result.IsSuccess
                && result.Path.Cells.Count == 1
                && result.Path.Cells[0] == s.Origin
                && result.Path.StepCount == 0;
        }, iter: Iterations);
    }

    private static bool AreFourAdjacent(Cell a, Cell b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;
}
