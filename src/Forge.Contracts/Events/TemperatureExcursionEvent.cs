namespace Forge.Contracts.Events;

/// <summary>
/// Raised when a temperature reading falls outside a lot's allowable range (Req 2.3, 27.4).
/// </summary>
public sealed record TemperatureExcursionEvent(Guid LotId, decimal Celsius, DateTimeOffset At);
