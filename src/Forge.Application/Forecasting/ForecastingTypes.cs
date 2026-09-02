using Forge.Domain.Common;

namespace Forge.Application.Forecasting;

/// <summary>
/// A demand forecast produced by <see cref="Abstractions.IMLPredictor"/> for a colony + gel type over
/// a horizon (Req 21.3, 21.4). Minimal shape sufficient for the ML seam; the concrete statistical
/// baseline is task 31.2. When the predictor is unavailable the orchestrator returns a fallback
/// forecast flagged via <see cref="IsFallback"/> (Req 21.4, 21.5).
/// </summary>
/// <param name="Colony">The colony the forecast is for.</param>
/// <param name="GelType">The gel type the forecast is for.</param>
/// <param name="Horizon">The forecast horizon.</param>
/// <param name="ExpectedDemand">The forecast expected demand over the horizon.</param>
/// <param name="IsFallback">True when this is a non-ML fallback forecast (predictor unavailable).</param>
public sealed record DemandForecast(
    ColonyId Colony,
    GelTypeId GelType,
    TimeSpan Horizon,
    double ExpectedDemand,
    bool IsFallback);

/// <summary>
/// A normalized inventory-risk assessment produced by <see cref="Abstractions.IMLPredictor"/>
/// (Req 21.3). Minimal shape for the seam; the concrete baseline is task 31.2.
/// </summary>
/// <param name="Score">The risk score, normalized to <c>[0, 1]</c> (higher = greater risk).</param>
/// <param name="IsFallback">True when this is a non-ML fallback score (predictor unavailable).</param>
public sealed record RiskScore(double Score, bool IsFallback);

/// <summary>
/// A minimal snapshot of inventory the <see cref="Abstractions.IMLPredictor"/> assesses risk over
/// (Req 21.3). Kept intentionally small for the seam; expanded as the baseline (task 31.2) requires.
/// </summary>
/// <param name="AsOf">The simulated time the snapshot was taken.</param>
/// <param name="TotalOnHandQuantity">Total on-hand (non-expired) quantity across all lots.</param>
/// <param name="AtRiskLotCount">Number of lots currently flagged at-risk.</param>
/// <param name="ExpiredLotCount">Number of lots currently flagged expired.</param>
public sealed record InventorySnapshot(
    DateTimeOffset AsOf,
    int TotalOnHandQuantity,
    int AtRiskLotCount,
    int ExpiredLotCount);
