namespace Forge.Domain.Spatial;

/// <summary>
/// A two-dimensional grid of cells representing the physical warehouse floor
/// (Req 18.1). Temperature_Zones, Dock_Bays, pick faces, and travel aisles are
/// placed on it. The grid itself only answers whether a given cell is
/// traversable (an aisle an <see cref="Agent"/> may move through) or an obstacle.
/// </summary>
/// <remarks>
/// <para>
/// The grid is deterministic and pure — no randomness, no I/O, no mutable state.
/// Traversability is modeled by an immutable set of blocked (obstacle) cells;
/// every in-bounds cell not in that set is an aisle. Cells outside the
/// <c>[0, Width) x [0, Height)</c> extent are never traversable.
/// </para>
/// <para>
/// <see cref="Neighbors"/> enumerates the 4-connected neighbors of a cell in a
/// FIXED order (+X, −X, +Y, −Y). The A* planner (task 14.2) expands neighbors in
/// exactly this order so path planning is reproducible for identical inputs
/// (Req 18.7).
/// </para>
/// </remarks>
public sealed class WarehouseGrid
{
    private readonly HashSet<Cell> _blocked;

    /// <summary>The number of columns; valid X coordinates are <c>[0, Width)</c>.</summary>
    public int Width { get; }

    /// <summary>The number of rows; valid Y coordinates are <c>[0, Height)</c>.</summary>
    public int Height { get; }

    /// <summary>
    /// Creates a grid of the given extent where the supplied cells are obstacles
    /// (non-traversable) and all other in-bounds cells are traversable aisles.
    /// </summary>
    /// <param name="width">Column count; must be &gt;= 0.</param>
    /// <param name="height">Row count; must be &gt;= 0.</param>
    /// <param name="blockedCells">
    /// Cells that are obstacles. May be <c>null</c> or empty for an all-aisle grid.
    /// Out-of-bounds entries are harmless (they are already non-traversable).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="width"/> or <paramref name="height"/> is negative.
    /// </exception>
    public WarehouseGrid(int width, int height, IEnumerable<Cell>? blockedCells = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);

        Width = width;
        Height = height;
        _blocked = blockedCells is null ? new HashSet<Cell>() : new HashSet<Cell>(blockedCells);
    }

    /// <summary>
    /// Creates a grid from a boolean traversability map. <c>traversable[x, y] == true</c>
    /// marks an aisle; <c>false</c> marks an obstacle. The map's dimensions define the
    /// grid extent.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="traversable"/> is null.</exception>
    public static WarehouseGrid FromTraversabilityMap(bool[,] traversable)
    {
        ArgumentNullException.ThrowIfNull(traversable);

        int width = traversable.GetLength(0);
        int height = traversable.GetLength(1);
        var blocked = new List<Cell>();
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                if (!traversable[x, y])
                {
                    blocked.Add(new Cell(x, y));
                }
            }
        }

        return new WarehouseGrid(width, height, blocked);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="c"/> is within bounds and is not an
    /// obstacle. Cells outside <c>[0, Width) x [0, Height)</c> are never traversable.
    /// </summary>
    public bool IsTraversable(Cell c) => InBounds(c) && !_blocked.Contains(c);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="c"/> lies within the grid extent.
    /// </summary>
    public bool InBounds(Cell c) => c.X >= 0 && c.X < Width && c.Y >= 0 && c.Y < Height;

    /// <summary>
    /// Enumerates the traversable 4-connected neighbors of <paramref name="c"/> in the
    /// fixed order +X, −X, +Y, −Y. Non-traversable and out-of-bounds neighbors are
    /// omitted. The A* planner relies on this exact order for deterministic results
    /// (Req 18.7).
    /// </summary>
    public IEnumerable<Cell> Neighbors(Cell c)
    {
        var plusX = new Cell(c.X + 1, c.Y);
        if (IsTraversable(plusX)) yield return plusX;

        var minusX = new Cell(c.X - 1, c.Y);
        if (IsTraversable(minusX)) yield return minusX;

        var plusY = new Cell(c.X, c.Y + 1);
        if (IsTraversable(plusY)) yield return plusY;

        var minusY = new Cell(c.X, c.Y - 1);
        if (IsTraversable(minusY)) yield return minusY;
    }
}
