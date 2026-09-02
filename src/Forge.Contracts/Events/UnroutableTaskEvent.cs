namespace Forge.Contracts.Events;

/// <summary>
/// Raised when no traversable path exists between a task's origin and destination (Req 2.3, 27.4).
/// </summary>
public sealed record UnroutableTaskEvent(Guid TaskId, int Ox, int Oy, int Dx, int Dy);
