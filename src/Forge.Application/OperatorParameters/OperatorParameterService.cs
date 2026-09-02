using System.Globalization;
using Forge.Application.Abstractions;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.Common;

namespace Forge.Application.OperatorParameters;

/// <summary>
/// Validates and applies operator-parameter changes against the live
/// <see cref="OperatorParameterState"/> (Req 20.2–20.8; design "Operator Parameters").
/// <para>
/// The operator submits one change at a time as an <see cref="OperatorParameterDto"/> (a key plus a
/// string value). <see cref="Apply"/> validates the value's type and range using
/// <see cref="OperatorParameterRanges"/> and, on success, updates the live state. On failure it
/// rejects, leaves the previous value in place, and returns
/// <see cref="DomainError.Validation(string, string?)"/> naming the invalid parameter (Req 20.8).
/// </para>
/// <para>
/// <b>Core-applied vs driver-routed.</b> Three parameters target driver-owned concerns and are
/// applied through the appropriate seam (design "Operator Parameters"):
/// <list type="bullet">
///   <item><b>Sim speed</b> → configures the accelerated <see cref="IClock"/> when one is injected
///     (real-time when the requested speed is 1, paused when 0, accelerated otherwise) (Req 20.2).</item>
///   <item><b>Inbound arrival rate</b> → routed to the driver's arrival generator; here it is held in
///     state for the driver to read, since the generator is wired in the composition root/driver
///     (tasks 27/33) (Req 20.5).</item>
///   <item><b>Demand multiplier</b> → routed to the driver's demand simulator; held in state for the
///     same reason (Req 20.6).</item>
/// </list>
/// The remaining three — <b>workers on shift</b>, <b>open dock bays</b>, and <b>slotting strategy</b>
/// — are applied directly to core state (Req 20.3, 20.4, 20.7). In every case the new value is stored
/// on <see cref="OperatorParameterState"/> so it is visible to the snapshot and Real_Time publish.
/// </para>
/// </summary>
public sealed class OperatorParameterService
{
    private readonly OperatorParameterState _state;
    private readonly IClock? _clock;

    /// <summary>
    /// Create the service over the live <paramref name="state"/>. The optional <paramref name="clock"/>
    /// is the driver seam for the sim-speed parameter: when supplied, a validated sim-speed change is
    /// applied to the clock via <see cref="IClock.Configure"/> (Req 20.2). When omitted (e.g. the
    /// driver wires the clock later in the composition root), the value is still applied to state so
    /// the driver can read it.
    /// </summary>
    public OperatorParameterService(OperatorParameterState state, IClock? clock = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _clock = clock;
    }

    /// <summary>The live operator-parameter state this service validates against and applies to.</summary>
    public OperatorParameterState State => _state;

    /// <summary>
    /// Validate and apply a single operator-parameter change (Req 20.2–20.8). Returns
    /// <see cref="Result.Success()"/> when the value is of the correct type and within range and the
    /// live state has been updated; otherwise returns a <see cref="DomainError.Validation(string, string?)"/>
    /// naming the invalid parameter, with the previous value retained unchanged.
    /// </summary>
    public Result Apply(OperatorParameterDto change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return change.Key switch
        {
            OperatorParameterKey.SimSpeed => ApplySimSpeed(change.Value),
            OperatorParameterKey.WorkersOnShift => ApplyWorkersOnShift(change.Value),
            OperatorParameterKey.OpenDockBays => ApplyOpenDockBays(change.Value),
            OperatorParameterKey.InboundRate => ApplyInboundRate(change.Value),
            OperatorParameterKey.DemandMultiplier => ApplyDemandMultiplier(change.Value),
            OperatorParameterKey.SlottingStrategy => ApplySlottingStrategy(change.Value),
            _ => DomainError.Validation(
                $"Unknown operator parameter '{change.Key}'.",
                change.Key),
        };
    }

    private Result ApplySimSpeed(string raw)
    {
        if (!TryParseDouble(raw, out var value) || !OperatorParameterRanges.IsValidSimSpeed(value))
        {
            return Reject(OperatorParameterKey.SimSpeed, raw);
        }

        _state.SetSimSpeed(value);

        // Driver-routed: configure the accelerated clock through the seam when one is wired in.
        if (_clock is not null)
        {
            var (mode, factor) = ClockConfigFor(value);
            _clock.Configure(mode, factor);
        }

        return Result.Success();
    }

    private Result ApplyWorkersOnShift(string raw)
    {
        if (!TryParseInt(raw, out var value) ||
            !OperatorParameterRanges.IsValidWorkersOnShift(value, _state.WorkerMax))
        {
            return Reject(OperatorParameterKey.WorkersOnShift, raw);
        }

        _state.SetWorkersOnShift(value);
        return Result.Success();
    }

    private Result ApplyOpenDockBays(string raw)
    {
        if (!TryParseInt(raw, out var value) ||
            !OperatorParameterRanges.IsValidOpenDockBays(value, _state.ModeledDockBays))
        {
            return Reject(OperatorParameterKey.OpenDockBays, raw);
        }

        _state.SetOpenDockBays(value);
        return Result.Success();
    }

    private Result ApplyInboundRate(string raw)
    {
        if (!TryParseDouble(raw, out var value) || !OperatorParameterRanges.IsValidInboundRate(value))
        {
            return Reject(OperatorParameterKey.InboundRate, raw);
        }

        // Driver-routed: held in state for the arrival generator (wired in tasks 27/33) to read.
        _state.SetInboundRate(value);
        return Result.Success();
    }

    private Result ApplyDemandMultiplier(string raw)
    {
        if (!TryParseDouble(raw, out var value) || !OperatorParameterRanges.IsValidDemandMultiplier(value))
        {
            return Reject(OperatorParameterKey.DemandMultiplier, raw);
        }

        // Driver-routed: held in state for the demand simulator (wired in tasks 27/33) to read.
        _state.SetDemandMultiplier(value);
        return Result.Success();
    }

    private Result ApplySlottingStrategy(string raw)
    {
        if (!OperatorParameterRanges.IsValidSlottingStrategy(raw))
        {
            return Reject(OperatorParameterKey.SlottingStrategy, raw);
        }

        _state.SetSlottingStrategy(raw);
        return Result.Success();
    }

    /// <summary>Map a validated sim-speed value onto a clock mode + acceleration factor (Req 20.2, 10.5).</summary>
    private static (ClockMode Mode, double Factor) ClockConfigFor(double simSpeed) => simSpeed switch
    {
        0.0 => (ClockMode.Paused, 1.0),
        1.0 => (ClockMode.RealTime, 1.0),
        _ => (ClockMode.Accelerated, simSpeed),
    };

    private static Result Reject(string parameter, string raw) =>
        DomainError.Validation(
            $"Operator parameter '{parameter}' rejected: '{raw}' is out of range or of an invalid type.",
            parameter);

    // Numeric parsing is invariant-culture and rejects thousands separators / leading+trailing
    // whitespace differences that would make a value ambiguous, so a wrong-type value (e.g. "abc",
    // "true", "") is rejected as a type failure (Req 20.8).
    private static bool TryParseDouble(string? raw, out double value)
    {
        value = 0.0;
        return raw is not null && double.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryParseInt(string? raw, out int value)
    {
        value = 0;
        return raw is not null && int.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }
}
