namespace Forge.Domain.Spatial;

/// <summary>
/// A single step between two adjacent cells on a <see cref="Path"/> (Req 18.1,
/// 19). An <see cref="Agent"/> reserves a Path_Segment for the interval it
/// occupies that step; the reservation ledger (task 15) keys mutual-exclusion
/// intervals on this type.
/// </summary>
public sealed record PathSegment(Cell From, Cell To);
