using Forge.Contracts.OperatorParameters;

namespace Forge.Application.OperatorParameters;

/// <summary>
/// Deployment-configured bounds and initial values for the operator-parameter state
/// (Req 20.1, 20.3, 20.4). The two bounds that are not fixed constants —
/// the maximum number of workers on shift and the count of physically modeled dock bays —
/// are supplied here so validation can enforce <c>[0, WorkerMax]</c> and
/// <c>[0, ModeledDockBays]</c> at runtime (the lower bounds live in
/// <see cref="OperatorParameterRanges"/>).
/// <para>
/// The initial values seed the live <see cref="OperatorParameterState"/> on construction and are
/// re-applied by <see cref="OperatorParameterState.Reset"/>. When an initial value is omitted the
/// state falls back to a sensible default (real-time speed, all configured workers/bays available,
/// unit inbound rate and demand multiplier, the velocity-affinity slotting strategy).
/// </para>
/// </summary>
public sealed class OperatorParameterOptions
{
    /// <summary>The configured maximum number of workers on shift (upper bound for Req 20.3). Must be &gt;= 0.</summary>
    public int WorkerMax { get; init; }

    /// <summary>The count of physically modeled dock bays (upper bound for Req 20.4). Must be &gt;= 0.</summary>
    public int ModeledDockBays { get; init; }

    /// <summary>Initial simulation speed (Req 20.2). Defaults to real-time (1.0) when null.</summary>
    public double? InitialSimSpeed { get; init; }

    /// <summary>Initial workers on shift (Req 20.3). Defaults to <see cref="WorkerMax"/> when null.</summary>
    public int? InitialWorkersOnShift { get; init; }

    /// <summary>Initial open dock bays (Req 20.4). Defaults to <see cref="ModeledDockBays"/> when null.</summary>
    public int? InitialOpenDockBays { get; init; }

    /// <summary>Initial inbound arrival rate (Req 20.5). Defaults to unit rate (1.0) when null.</summary>
    public double? InitialInboundRate { get; init; }

    /// <summary>Initial colony demand multiplier (Req 20.6). Defaults to unit multiplier (1.0) when null.</summary>
    public double? InitialDemandMultiplier { get; init; }

    /// <summary>Initial slotting strategy key (Req 20.7). Defaults to velocity-affinity when null.</summary>
    public string? InitialSlottingStrategy { get; init; }
}
