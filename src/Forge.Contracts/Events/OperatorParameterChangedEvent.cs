using Forge.Contracts.Dtos;

namespace Forge.Contracts.Events;

/// <summary>
/// Raised when an operator parameter changes, carrying the full new state (Req 2.3, 27.4, 20.9).
/// </summary>
public sealed record OperatorParameterChangedEvent(OperatorParameterStateDto State);
