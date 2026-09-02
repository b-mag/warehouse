using Forge.Application.Abstractions;
using Forge.Domain.Common;

namespace Forge.Application.OperatorParameters;

/// <summary>
/// The update-operator-parameter use-case handler (task 24.7; Req 20.8, 20.9; design "Operator
/// Parameters"). An operator submits a single change as a
/// <see cref="Forge.Contracts.OperatorParameters.OperatorParameterDto"/> (a key plus a string
/// value); this handler validates and applies it to the live system, then publishes the updated
/// parameter state so all connected clients converge.
/// <para>
/// <b>Validate + apply-to-live (Req 20.8).</b> Validation and the apply-to-live decision are
/// delegated to <see cref="OperatorParameterService"/>, which owns the per-parameter ranges/types
/// and the core-applied vs. driver-routed distinction. On a valid change it mutates the live
/// <see cref="OperatorParameterState"/> (and configures the accelerated clock for a sim-speed
/// change when one is wired into the service); on an invalid or out-of-range value it rejects,
/// retains the previous value, and returns a <see cref="DomainError.Validation(string, string?)"/>
/// naming the invalid parameter. Because a rejection never mutates state, the previous value is
/// retained by construction.
/// </para>
/// <para>
/// <b>Publish updated parameter state (Req 20.9).</b> Only on a successful apply does the handler
/// publish an <see cref="OperatorParameterChanged"/> event through <see cref="IEventBus"/>, carrying
/// the full post-change <see cref="Forge.Contracts.Dtos.OperatorParameterStateDto"/> and the current
/// time from <see cref="IClock"/>. The Real_Time publisher (Infrastructure task 32) subscribes to
/// the bus and forwards it to clients as the Contracts transport event. A rejected change publishes
/// nothing, so clients are never notified of a change that did not take effect.
/// </para>
/// <para>
/// The publish happens <em>after</em> the state has been mutated, so the event always reflects the
/// applied state. Publishing is awaited but is decoupled from the operator's request only insofar as
/// the event bus itself is decoupled from Real_Time delivery (the in-process bus / SignalR publisher
/// never block the tick loop — design "Real-time state distribution").
/// </para>
/// </summary>
public sealed class UpdateOperatorParameterHandler
{
    private readonly OperatorParameterService _service;
    private readonly IEventBus _eventBus;
    private readonly IClock _clock;

    /// <summary>
    /// Construct the handler from the parameter service it validates/applies through, the event bus
    /// it publishes the updated state on, and the clock it stamps the published event with.
    /// </summary>
    /// <param name="service">Validates and applies a change to the live operator-parameter state (Req 20.8).</param>
    /// <param name="eventBus">Publishes the updated parameter state on success (Req 20.9).</param>
    /// <param name="clock">Supplies the current time stamped onto the published event.</param>
    public UpdateOperatorParameterHandler(
        OperatorParameterService service,
        IEventBus eventBus,
        IClock clock)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Validate and apply the operator-parameter <paramref name="change"/> to the live system, then
    /// publish the updated parameter state on success (Req 20.8, 20.9).
    /// </summary>
    /// <param name="change">The single parameter change (key + string value) the operator submitted.</param>
    /// <param name="ct">Cancellation token propagated to the event-bus publish.</param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the value is of the correct type and within range, the live
    /// state has been updated, and the updated-state event has been published; otherwise a
    /// <see cref="DomainError.Validation(string, string?)"/> naming the invalid parameter, with the
    /// previous value retained and nothing published.
    /// </returns>
    public async Task<Result> Handle(
        Forge.Contracts.OperatorParameters.OperatorParameterDto change,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        // Validate + apply-to-live (Req 20.8). A rejection leaves the live state unchanged, so the
        // previous value is retained; we return the naming error without publishing anything.
        var result = _service.Apply(change);
        if (result.IsFailure)
        {
            return result;
        }

        // Publish the updated parameter state so all connected clients converge (Req 20.9). The DTO is
        // projected from the just-mutated live state, so the event reflects exactly what was applied.
        var evt = new OperatorParameterChanged(_service.State.ToDto(), _clock.Now);
        await _eventBus.PublishAsync(evt, ct).ConfigureAwait(false);

        return result;
    }
}
