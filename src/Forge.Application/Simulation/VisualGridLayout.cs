using Forge.Domain.Spatial;

namespace Forge.Application.Simulation;

/// <summary>
/// Shared Phase-1 grid ↔ scene layout constants (must stay aligned with
/// <c>src/Forge.Game/web/src/lib/layout.ts</c> and agent cellToWorld).
/// </summary>
public static class VisualGridLayout
{
    public const int GridWidthCells = 32;
    public const int GridHeightCells = 32;
    public const double CellWorld = 1.1;
    public const int ZoneSizeWorld = 8;
    public const int ZoneGapWorld = 2;
    public const int ZonePitchWorld = ZoneSizeWorld + ZoneGapWorld;

    /// <summary>Inbound maglev rail row (low-Y edge, opposite ship berths).</summary>
    public const int RailY = 0;

    /// <summary>Preferred pickup column along the rail.</summary>
    public const int RailPickupX = 4;

    /// <summary>Idle breakroom (workers wait here between tasks).</summary>
    public const int IdleBayMinX = 22;
    public const int IdleBayMaxX = 28;
    public const int IdleBayMinY = 2;
    public const int IdleBayMaxY = 5;

    /// <summary>
    /// Half-extent of blocked zone shelving in cells. Zone footprint is ~ZONE_SIZE/CELL_WORLD ≈ 7
    /// cells across; block the full shelf so workers path around (never through) holding areas.
    /// </summary>
    public const int ZoneBlockHalf = 3;

    public static Cell ReceivingPickupCell(WarehouseGrid grid) =>
        new(Math.Clamp(RailPickupX, 0, Math.Max(0, grid.Width - 1)),
            Math.Clamp(RailY, 0, Math.Max(0, grid.Height - 1)));

    public static bool IsInIdleBay(Cell cell) =>
        cell.X >= IdleBayMinX && cell.X <= IdleBayMaxX &&
        cell.Y >= IdleBayMinY && cell.Y <= IdleBayMaxY;

    public static Cell IdleBaySlot(WarehouseGrid grid, Agent agent)
    {
        int width = Math.Max(1, IdleBayMaxX - IdleBayMinX + 1);
        int height = Math.Max(1, IdleBayMaxY - IdleBayMinY + 1);
        uint fold = StableGuidFold(agent.Id.Value);
        int x = IdleBayMinX + (int)(fold % (uint)width);
        int y = IdleBayMinY + (int)((fold / (uint)width) % (uint)height);
        x = Math.Clamp(x, 0, Math.Max(0, grid.Width - 1));
        y = Math.Clamp(y, 0, Math.Max(0, grid.Height - 1));
        return new Cell(x, y);
    }

    /// <summary>
    /// Block entire zone shelf footprints so pathfinding routes through aisles only.
    /// Workers approach an entry cell on the aisle face and place/pick there — they never walk
    /// through the colored region (shelving).
    /// </summary>
    public static IReadOnlyList<Cell> BuildZoneBlockedCells(int zoneCount, int gridWidth, int gridHeight)
    {
        zoneCount = Math.Max(0, zoneCount);
        if (zoneCount == 0 || gridWidth <= 0 || gridHeight <= 0)
        {
            return Array.Empty<Cell>();
        }

        var blocked = new HashSet<Cell>();

        for (int i = 0; i < zoneCount; i++)
        {
            var center = ZoneCenterCell(i, zoneCount, gridWidth, gridHeight);
            for (int dx = -ZoneBlockHalf; dx <= ZoneBlockHalf; dx++)
            {
                for (int dy = -ZoneBlockHalf; dy <= ZoneBlockHalf; dy++)
                {
                    int x = center.X + dx;
                    int y = center.Y + dy;
                    if (x >= 0 && x < gridWidth && y >= 0 && y < gridHeight)
                    {
                        blocked.Add(new Cell(x, y));
                    }
                }
            }
        }

        return blocked.OrderBy(c => c.Y).ThenBy(c => c.X).ToArray();
    }

    public static Cell ZoneCenterCell(int zoneIndex, int zoneCount, int gridWidth, int gridHeight)
    {
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(zoneCount)));
        int rows = Math.Max(1, (int)Math.Ceiling(zoneCount / (double)cols));
        double originXWorld = (-(cols - 1) * ZonePitchWorld) / 2.0;
        double originZWorld = (-(rows - 1) * ZonePitchWorld) / 2.0;
        int col = zoneIndex % cols;
        int row = zoneIndex / cols;
        double centerXWorld = originXWorld + col * ZonePitchWorld;
        double centerZWorld = originZWorld + row * ZonePitchWorld;
        double gridCenterX = (gridWidth - 1) / 2.0;
        double gridCenterY = (gridHeight - 1) / 2.0;
        int xCell = (int)Math.Round(centerXWorld / CellWorld + gridCenterX);
        int yCell = (int)Math.Round(centerZWorld / CellWorld + gridCenterY);
        return new Cell(
            Math.Clamp(xCell, 0, gridWidth - 1),
            Math.Clamp(yCell, 0, gridHeight - 1));
    }

    /// <summary>
    /// Aisle face just outside the blocked shelf — where workers stand to place/pick.
    /// Prefer the south face (toward rail / breakroom).
    /// </summary>
    public static Cell ZoneEntryCell(int zoneIndex, int zoneCount, WarehouseGrid grid)
    {
        var center = ZoneCenterCell(zoneIndex, zoneCount, grid.Width, grid.Height);
        var candidates = new[]
        {
            new Cell(center.X, Math.Clamp(center.Y - ZoneBlockHalf - 1, 0, grid.Height - 1)),
            new Cell(center.X, Math.Clamp(center.Y + ZoneBlockHalf + 1, 0, grid.Height - 1)),
            new Cell(Math.Clamp(center.X - ZoneBlockHalf - 1, 0, grid.Width - 1), center.Y),
            new Cell(Math.Clamp(center.X + ZoneBlockHalf + 1, 0, grid.Width - 1), center.Y),
        };

        foreach (var c in candidates)
        {
            if (grid.IsTraversable(c))
            {
                return c;
            }
        }

        // Last resort: search outward for any walkable neighbor.
        for (int r = ZoneBlockHalf + 1; r <= ZoneBlockHalf + 4; r++)
        {
            foreach (var c in new[]
                     {
                         new Cell(center.X, center.Y - r),
                         new Cell(center.X, center.Y + r),
                         new Cell(center.X - r, center.Y),
                         new Cell(center.X + r, center.Y),
                     })
            {
                if (c.X >= 0 && c.X < grid.Width && c.Y >= 0 && c.Y < grid.Height &&
                    grid.IsTraversable(c))
                {
                    return c;
                }
            }
        }

        return center;
    }

    public static Cell ZoneEntryCellForId(
        Guid zoneId,
        IReadOnlyList<Guid> orderedZoneIds,
        WarehouseGrid grid)
    {
        int index = -1;
        for (int i = 0; i < orderedZoneIds.Count; i++)
        {
            if (orderedZoneIds[i] == zoneId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return ReceivingPickupCell(grid);
        }

        return ZoneEntryCell(index, orderedZoneIds.Count, grid);
    }

    private static uint StableGuidFold(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        uint hash = 2166136261u;
        foreach (var b in bytes)
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return hash;
    }
}
