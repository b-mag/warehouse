namespace Forge.Contracts.Events;

/// <summary>
/// Raised when a receiving/outbound backlog size changes (Req 2.3, 27.4).
/// </summary>
public sealed record BacklogChangedEvent(string Kind, int NewSize);
