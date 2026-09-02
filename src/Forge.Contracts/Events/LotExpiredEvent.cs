namespace Forge.Contracts.Events;

/// <summary>
/// Raised on the non-expired to expired transition of a gel lot (Req 2.3, 27.4).
/// </summary>
public sealed record LotExpiredEvent(Guid LotId, DateTimeOffset At);
