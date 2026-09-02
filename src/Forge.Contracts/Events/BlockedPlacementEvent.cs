namespace Forge.Contracts.Events;

/// <summary>
/// Raised when a lot cannot be slotted into any compatible zone (Req 2.3, 27.4).
/// </summary>
public sealed record BlockedPlacementEvent(Guid LotId, string Reason);
