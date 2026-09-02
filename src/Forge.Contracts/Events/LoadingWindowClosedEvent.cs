namespace Forge.Contracts.Events;

/// <summary>
/// Raised on loading-window close, reporting loaded quantity and shortfall (Req 2.3, 27.4).
/// </summary>
public sealed record LoadingWindowClosedEvent(Guid StarshipId, int Loaded, int Shortfall);
