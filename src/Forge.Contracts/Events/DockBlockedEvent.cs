namespace Forge.Contracts.Events;

/// <summary>
/// Raised when a dock bay is occupied and a competing operation is queued (Req 2.3, 27.4).
/// </summary>
public sealed record DockBlockedEvent(Guid DockBayId, DateTimeOffset At);
