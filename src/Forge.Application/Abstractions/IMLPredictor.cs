using Forge.Application.Forecasting;
using Forge.Domain.Common;

namespace Forge.Application.Abstractions;

/// <summary>
/// The machine-learning seam (design "WMS Core Application abstractions"; Req 21). In Phase 1 a
/// statistical baseline (in <c>Forge.Infrastructure</c>, task 31.2) implements it; Phase 2 could
/// swap in ML.NET/ONNX without any change to the core. When <see cref="IsAvailable"/> is false the
/// forecasting orchestrator returns fallback results flagged as such (Req 21.4, 21.5).
/// </summary>
public interface IMLPredictor
{
    /// <summary>Whether the predictor is currently available (Req 21.4).</summary>
    bool IsAvailable { get; }

    /// <summary>Forecast expected demand for a colony + gel type over a horizon (Req 21.3).</summary>
    Task<DemandForecast> ForecastDemandAsync(ColonyId colony, GelTypeId gelType, TimeSpan horizon, CancellationToken ct);

    /// <summary>Assess inventory risk from a snapshot (Req 21.3).</summary>
    Task<RiskScore> AssessInventoryRiskAsync(InventorySnapshot snapshot, CancellationToken ct);
}
