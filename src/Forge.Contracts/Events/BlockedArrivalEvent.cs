namespace Forge.Contracts.Events;

/// <summary>
/// Raised when an inbound arrival cannot be received (e.g., no dock slot) (Req 2.3, 27.4).
/// </summary>
public sealed record BlockedArrivalEvent(Guid LotId, string Reason);
