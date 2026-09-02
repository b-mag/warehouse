using Forge.Contracts.Dtos;
using Forge.Domain.Events;

namespace Forge.Application.OperatorParameters;

/// <summary>
/// Application-layer domain event raised when an operator-parameter change has been validated and
/// applied to the live <see cref="OperatorParameterState"/> (Req 20.9). It carries the full new
/// <see cref="OperatorParameterStateDto"/> so every connected client can converge on the updated
/// state without re-deriving it.
/// <para>
/// <b>Why this lives in the Application layer, not the Domain.</b> The BCL-only Domain intentionally
/// does <em>not</em> model this event: it would have to reference a <c>Forge.Contracts</c> DTO, which
/// the Domain must not do (see the remarks in <c>Forge.Domain.Events.DomainEvents</c>). A parameter
/// change is also not produced by any pure domain rule — it originates from an operator command
/// handled by <see cref="UpdateOperatorParameterHandler"/>. It therefore implements the
/// <see cref="IDomainEvent"/> marker so it flows through the existing <c>IEventBus</c> seam, but the
/// record itself is owned here.
/// </para>
/// <para>
/// The Real_Time publisher (Infrastructure task 32) subscribes to the event bus and maps this event
/// onto the transport <c>Forge.Contracts.Events.OperatorParameterChangedEvent</c> pushed to clients
/// (Req 20.9, 23.4). Because the DTO is already the Contracts projection, that mapping is a direct
/// wrap.
/// </para>
/// </summary>
/// <param name="State">The full operator-parameter state after the applied change.</param>
/// <param name="OccurredAt">When the change was applied, in the core's notion of "now".</param>
public sealed record OperatorParameterChanged(
    OperatorParameterStateDto State,
    DateTimeOffset OccurredAt) : IDomainEvent;
