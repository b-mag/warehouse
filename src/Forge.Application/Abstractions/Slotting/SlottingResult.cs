using Forge.Domain.Common;

namespace Forge.Application.Abstractions.Slotting;

/// <summary>
/// The outcome of an <see cref="ISlottingStrategy.SelectZone"/> call (design "WMS Core Application
/// abstractions"; Req 16.1, 16.2, 16.3): the chosen <see cref="ZoneId"/> on success, or an
/// unslottable failure when no compatible zone with capacity exists.
/// <para>
/// This is a thin wrapper over the domain's established <see cref="Result{T}"/> of
/// <see cref="ZoneId"/> so the strategy seam speaks the same typed-error result the domain slotting
/// primitives (<c>Forge.Domain.Slotting.SlottingCandidates</c>) already return — an unslottable
/// outcome is carried as a <see cref="DomainError"/> of <see cref="ErrorKind.Unslottable"/>.
/// Raising the <c>BlockedPlacement</c> event on an unslottable result is the put-away handler's
/// job (task 18.1 / 24.2), not the strategy's.
/// </para>
/// </summary>
public readonly record struct SlottingResult(Result<ZoneId> Outcome)
{
    /// <summary>True when a zone was selected.</summary>
    public bool IsSuccess => Outcome.IsSuccess;

    /// <summary>True when no compatible zone with capacity exists (unslottable).</summary>
    public bool IsUnslottable => Outcome.IsFailure;

    /// <summary>The selected zone. Throws if accessed on an unslottable result — check <see cref="IsSuccess"/> first.</summary>
    public ZoneId Zone => Outcome.Value;

    /// <summary>The unslottable error. Throws if accessed on a success.</summary>
    public DomainError Error => Outcome.Error;

    /// <summary>A successful result carrying the chosen <paramref name="zone"/>.</summary>
    public static SlottingResult Slotted(ZoneId zone) => new(Result.Success(zone));

    /// <summary>An unslottable result carrying the given <paramref name="error"/>.</summary>
    public static SlottingResult Unslottable(DomainError error) => new(Result.Failure<ZoneId>(error));

    /// <summary>Lift a domain <see cref="Result{T}"/> of <see cref="ZoneId"/> into a <see cref="SlottingResult"/>.</summary>
    public static implicit operator SlottingResult(Result<ZoneId> outcome) => new(outcome);
}
