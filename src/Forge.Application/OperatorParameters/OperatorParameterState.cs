using Forge.Contracts.Dtos;
using Forge.Contracts.OperatorParameters;

namespace Forge.Application.OperatorParameters;

/// <summary>
/// Holds the live values of the six operator-adjustable parameters (Req 20.1): simulation speed,
/// workers on shift, open dock bays, inbound arrival rate, colony demand multiplier, and the active
/// slotting-strategy key. The state also owns the configured bounds that validation depends on —
/// the worker maximum and the count of physically modeled dock bays — supplied via
/// <see cref="OperatorParameterOptions"/> (Req 20.3, 20.4).
/// <para>
/// This type carries only the current values and bounds; validation and the apply-to-live decision
/// are the responsibility of <see cref="OperatorParameterService"/>. On a rejected change the
/// service never mutates this state, so the previous value is retained by construction (Req 20.8).
/// </para>
/// </summary>
public sealed class OperatorParameterState
{
    /// <summary>Default simulation speed when no initial value is configured: real-time (Req 20.2).</summary>
    public const double DefaultSimSpeed = 1.0;

    /// <summary>Default inbound arrival rate when no initial value is configured (Req 20.5).</summary>
    public const double DefaultInboundRate = 1.0;

    /// <summary>Default colony demand multiplier when no initial value is configured (Req 20.6).</summary>
    public const double DefaultDemandMultiplier = 1.0;

    /// <summary>Default slotting strategy when no initial value is configured (Req 20.7).</summary>
    public const string DefaultSlottingStrategy = SlottingStrategyKey.VelocityAffinity;

    private readonly OperatorParameterOptions _options;

    /// <summary>
    /// Create the state from configured bounds and initial values. Throws
    /// <see cref="ArgumentException"/> only for a genuinely misconfigured deployment (negative
    /// bounds, or initial values outside the configured bounds) — an operator-supplied change is
    /// never routed through this constructor; it goes through <see cref="OperatorParameterService"/>.
    /// </summary>
    public OperatorParameterState(OperatorParameterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.WorkerMax < OperatorParameterRanges.WorkersOnShiftMin)
        {
            throw new ArgumentException(
                $"WorkerMax ({options.WorkerMax}) must be >= {OperatorParameterRanges.WorkersOnShiftMin}.",
                nameof(options));
        }

        if (options.ModeledDockBays < OperatorParameterRanges.OpenDockBaysMin)
        {
            throw new ArgumentException(
                $"ModeledDockBays ({options.ModeledDockBays}) must be >= {OperatorParameterRanges.OpenDockBaysMin}.",
                nameof(options));
        }

        _options = options;
        Reset();
    }

    /// <summary>The configured maximum number of workers on shift (upper bound for Req 20.3).</summary>
    public int WorkerMax => _options.WorkerMax;

    /// <summary>The count of physically modeled dock bays (upper bound for Req 20.4).</summary>
    public int ModeledDockBays => _options.ModeledDockBays;

    /// <summary>Current simulation speed: 0 = paused, 1 = real-time, &gt;1 = accelerated (Req 20.2).</summary>
    public double SimSpeed { get; private set; }

    /// <summary>Current number of workers on shift (Req 20.3).</summary>
    public int WorkersOnShift { get; private set; }

    /// <summary>Current number of open dock bays (Req 20.4).</summary>
    public int OpenDockBays { get; private set; }

    /// <summary>Current inbound arrival rate (Req 20.5).</summary>
    public double InboundRate { get; private set; }

    /// <summary>Current colony demand multiplier (Req 20.6).</summary>
    public double DemandMultiplier { get; private set; }

    /// <summary>Current active slotting-strategy key (Req 20.7).</summary>
    public string SlottingStrategy { get; private set; } = DefaultSlottingStrategy;

    /// <summary>
    /// Reset every parameter to its configured initial value (or its default when unconfigured).
    /// Initial values are clamped to the configured bounds so a valid deployment always starts in a
    /// valid state.
    /// </summary>
    public void Reset()
    {
        SimSpeed = _options.InitialSimSpeed ?? DefaultSimSpeed;
        WorkersOnShift = _options.InitialWorkersOnShift ?? _options.WorkerMax;
        OpenDockBays = _options.InitialOpenDockBays ?? _options.ModeledDockBays;
        InboundRate = _options.InitialInboundRate ?? DefaultInboundRate;
        DemandMultiplier = _options.InitialDemandMultiplier ?? DefaultDemandMultiplier;
        SlottingStrategy = _options.InitialSlottingStrategy ?? DefaultSlottingStrategy;
    }

    // ---- Mutators used only by OperatorParameterService after a value has been validated ----

    internal void SetSimSpeed(double value) => SimSpeed = value;

    internal void SetWorkersOnShift(int value) => WorkersOnShift = value;

    internal void SetOpenDockBays(int value) => OpenDockBays = value;

    internal void SetInboundRate(double value) => InboundRate = value;

    internal void SetDemandMultiplier(double value) => DemandMultiplier = value;

    internal void SetSlottingStrategy(string value) => SlottingStrategy = value;

    /// <summary>
    /// Project the current values into the immutable <see cref="OperatorParameterStateDto"/> the
    /// snapshot and the Real_Time publish carry (Req 2.3, 20.9, 23.4). The publish itself is wired in
    /// the driver/composition root (tasks 24.7 / 33.1); this method only exposes the current values.
    /// </summary>
    public OperatorParameterStateDto ToDto() => new(
        SimSpeed,
        WorkersOnShift,
        OpenDockBays,
        InboundRate,
        DemandMultiplier,
        SlottingStrategy);
}
