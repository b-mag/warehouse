namespace Forge.Domain.Spatial;

/// <summary>
/// A single cell on the <see cref="WarehouseGrid"/>, addressed by integer
/// coordinates (Req 18.1). Cells host Temperature_Zones, Dock_Bays, pick faces,
/// and travel aisles.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IComparable{T}"/> gives cells a total order in <c>(X, Y)</c>
/// lexicographic ordering. The A* planner (task 14.2) uses this ordering as the
/// final open-set tie-break — after <c>f = g + h</c> and lower <c>h</c> — so the
/// planner is fully deterministic with no reliance on hash-set iteration order
/// (Req 18.7). Keeping the comparison here, next to the type, keeps that
/// tie-break well-defined for every consumer.
/// </para>
/// </remarks>
public readonly record struct Cell(int X, int Y) : IComparable<Cell>
{
    /// <summary>
    /// Orders cells lexicographically by <see cref="X"/> then <see cref="Y"/>.
    /// </summary>
    public int CompareTo(Cell other)
    {
        int byX = X.CompareTo(other.X);
        return byX != 0 ? byX : Y.CompareTo(other.Y);
    }

    public override string ToString() => $"({X}, {Y})";
}
