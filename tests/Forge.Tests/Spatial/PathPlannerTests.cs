using Forge.Domain.Common;
using Forge.Domain.Spatial;

namespace Forge.Tests.Spatial;

// Unit tests for the deterministic A* path planner (task 14.4).
//
// Covers the unroutable cases (destination walled off, untraversable endpoint), traversal-time
// derivation from path length and agent speed, and the trivial origin==destination path.
//
// Validates: Requirements 18.5, 18.6, 28.1
public sealed class PathPlannerTests
{
    private readonly AStarPathPlanner _planner = new();

    // Req 18.6: a destination completely walled off by obstacles is unroutable — the planner returns
    // an IsUnroutable result carrying a DomainError of kind Unroutable.
    [Fact]
    public void DestinationWalledOff_IsUnroutable()
    {
        // 3x3 grid; wall the destination (2,2) off by blocking its only two neighbors (1,2) and (2,1).
        var blocked = new[] { new Cell(1, 2), new Cell(2, 1) };
        var grid = new WarehouseGrid(3, 3, blocked);

        var result = _planner.Plan(grid, new Cell(0, 0), new Cell(2, 2));

        Assert.True(result.IsUnroutable);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Unroutable, result.Error.Kind);
    }

    // Req 18.6: an untraversable endpoint (origin or destination is itself an obstacle) can never be
    // part of any path, so the request is unroutable.
    [Fact]
    public void UntraversableDestination_IsUnroutable()
    {
        var grid = new WarehouseGrid(3, 3, new[] { new Cell(2, 2) });

        var result = _planner.Plan(grid, new Cell(0, 0), new Cell(2, 2));

        Assert.True(result.IsUnroutable);
        Assert.Equal(ErrorKind.Unroutable, result.Error.Kind);
    }

    [Fact]
    public void UntraversableOrigin_IsUnroutable()
    {
        var grid = new WarehouseGrid(3, 3, new[] { new Cell(0, 0) });

        var result = _planner.Plan(grid, new Cell(0, 0), new Cell(2, 2));

        Assert.True(result.IsUnroutable);
        Assert.Equal(ErrorKind.Unroutable, result.Error.Kind);
    }

    // Req 18.5: the travel time contributed to a task is derived from the traversal time of the
    // planned path. A straight path of N steps traversed at speed s takes N/s seconds.
    [Fact]
    public void StraightPath_TraversalTime_IsStepsOverSpeed()
    {
        // A single open row of 6 cells: origin (0,0) -> destination (5,0) is 5 steps.
        var grid = new WarehouseGrid(6, 1);

        var result = _planner.Plan(grid, new Cell(0, 0), new Cell(5, 0));

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Path.StepCount);

        const double cellsPerSecond = 2.0;
        Assert.Equal(TimeSpan.FromSeconds(5 / cellsPerSecond), result.Path.TraversalTime(cellsPerSecond));
    }

    // Req 18.5: verify the derivation across several step counts and speeds so the N/s relationship
    // is not an accident of one example.
    [Theory]
    [InlineData(3, 1.0)]
    [InlineData(4, 2.0)]
    [InlineData(7, 3.5)]
    public void TraversalTime_EqualsStepsDividedBySpeed(int steps, double cellsPerSecond)
    {
        var grid = new WarehouseGrid(steps + 1, 1);

        var result = _planner.Plan(grid, new Cell(0, 0), new Cell(steps, 0));

        Assert.True(result.IsSuccess);
        Assert.Equal(steps, result.Path.StepCount);
        Assert.Equal(TimeSpan.FromSeconds(steps / cellsPerSecond), result.Path.TraversalTime(cellsPerSecond));
    }

    // Req 18.5: origin == destination is a zero-step path whose traversal time is Zero regardless of
    // speed (a stationary agent is already there).
    [Fact]
    public void OriginEqualsDestination_IsZeroStepPathWithZeroTraversalTime()
    {
        var grid = new WarehouseGrid(4, 4);
        var cell = new Cell(2, 3);

        var result = _planner.Plan(grid, cell, cell);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Path.Cells);
        Assert.Equal(cell, result.Path.Cells[0]);
        Assert.Equal(0, result.Path.StepCount);
        Assert.Equal(TimeSpan.Zero, result.Path.TraversalTime(1.5));
    }
}
