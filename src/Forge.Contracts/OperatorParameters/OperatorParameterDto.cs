namespace Forge.Contracts.OperatorParameters;

/// <summary>
/// A single operator-parameter change request submitted by the operator (Req 20.1, 20.8).
/// The operator submits one change at a time: a <see cref="Key"/> identifying which
/// parameter to change (one of <see cref="OperatorParameterKey"/>) and the requested
/// <see cref="Value"/>. Numeric parameters carry the value as a string so the
/// Application layer can validate both type and range and return an error naming the
/// parameter when the value is non-numeric or out of range (Req 20.8).
/// </summary>
public sealed record OperatorParameterDto(string Key, string Value);
