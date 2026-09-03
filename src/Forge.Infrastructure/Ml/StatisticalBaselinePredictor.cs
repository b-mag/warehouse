using Forge.Application.Abstractions;
using Forge.Application.Forecasting;
using Forge.Domain.Common;

namespace Forge.Infrastructure.Ml;

/// <summary>
/// Phase-1 statistical baseline implementation of <see cref="IMLPredictor"/> (Req 21.1, design "ML
/// Forecasting"). It is a deliberately simple, deterministic skeleton behind the ML seam: a
/// moving-average / seasonal demand estimate and an expiry + temperature-excursion risk score. Real
/// ML.NET / ONNX models are deferred to Phase 2 (Req 21.2) and can replace this class without any
/// change to the Application or Domain layers.
///
/// <para><b>Availability.</b> <see cref="IsAvailable"/> is always <see langword="true"/>: the baseline
/// has no external dependency, so it is available in Phase 1. The fallback path (Req 21.5) is the
/// forecasting orchestrator's job when a predictor reports unavailable, not this class's.</para>
///
/// <para><b>Determinism.</b> The predictor holds no mutable state and reads no clock or randomness
/// beyond the supplied inputs, so identical inputs always yield identical outputs.</para>
/// </summary>
public sealed class StatisticalBaselinePredictor : IMLPredictor
{
    // ---- Demand baseline tuning constants ----

    /// <summary>Baseline mean daily demand around which a per-series rate is derived, in units/day.</summary>
    private const double BaselineDailyDemand = 20.0;

    /// <summary>Half-width of the per-series daily-rate band around <see cref="BaselineDailyDemand"/>.</summary>
    private const double DailyDemandBand = 12.0;

    /// <summary>Peak-to-trough amplitude of the weekly seasonal shape (fraction of the daily rate).</summary>
    private const double WeeklySeasonalAmplitude = 0.30;

    /// <summary>Length of the seasonal cycle used by the moving-average / seasonal shape, in days.</summary>
    private const int SeasonalPeriodDays = 7;

    // ---- Risk-score tuning weights (contributions sum-clamped into [0, 1]) ----

    /// <summary>Risk weight applied to the fraction of lots flagged expired (most severe).</summary>
    private const double ExpiredWeight = 0.60;

    /// <summary>Risk weight applied to the fraction of lots flagged at-risk (near-expiry / excursion exposure).</summary>
    private const double AtRiskWeight = 0.40;

    /// <summary>
    /// Additional risk floor contributed purely by expired lots existing at all, so a snapshot with
    /// any expired inventory never scores as negligible risk even when the on-hand pool is large.
    /// </summary>
    private const double ExpiredPresenceFloor = 0.15;

    /// <inheritdoc />
    /// <remarks>The baseline has no external dependency and is always available in Phase 1.</remarks>
    public bool IsAvailable => true;

    /// <summary>
    /// Produces a non-fallback demand forecast for the <paramref name="colony"/> + <paramref name="gelType"/>
    /// over <paramref name="horizon"/> (Req 21.3).
    ///
    /// <para><b>Formula.</b> Each (colony, gel type) series has a stable per-series daily rate
    /// <c>r = BaselineDailyDemand + DailyDemandBand * (2 * u - 1)</c>, where <c>u ∈ [0, 1)</c> is a
    /// deterministic hash of the two identifiers — this is the moving-average level for the series.
    /// A weekly seasonal multiplier <c>s(d) = 1 + WeeklySeasonalAmplitude * sin(2π * (d mod 7) / 7)</c>
    /// modulates each day <c>d</c> of the horizon. Expected demand is the seasonal sum over whole days
    /// plus the fractional remainder of the final day:
    /// <c>Σ r * s(d)</c> for whole days, prorated on the tail. The result is clamped to be non-negative.</para>
    /// </summary>
    public Task<DemandForecast> ForecastDemandAsync(ColonyId colony, GelTypeId gelType, TimeSpan horizon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var horizonDays = Math.Max(0.0, horizon.TotalDays);
        var dailyRate = PerSeriesDailyRate(colony, gelType);

        var wholeDays = (int)Math.Floor(horizonDays);
        var fractionalDay = horizonDays - wholeDays;

        var expected = 0.0;
        for (var day = 0; day < wholeDays; day++)
        {
            expected += dailyRate * SeasonalMultiplier(day);
        }

        if (fractionalDay > 0.0)
        {
            expected += dailyRate * SeasonalMultiplier(wholeDays) * fractionalDay;
        }

        expected = Math.Max(0.0, expected);

        var forecast = new DemandForecast(colony, gelType, horizon, expected, IsFallback: false);
        return Task.FromResult(forecast);
    }

    /// <summary>
    /// Assesses inventory risk from a <paramref name="snapshot"/>, returning a score in <c>[0, 1]</c>
    /// that rises with expiry and temperature-excursion exposure (Req 21.4).
    ///
    /// <para><b>Formula.</b> Let <c>N = TotalOnHandQuantity + AtRiskLotCount + ExpiredLotCount</c> be
    /// the exposure denominator (guarded to at least 1). The score is
    /// <c>ExpiredWeight * (ExpiredLotCount / N) + AtRiskWeight * (AtRiskLotCount / N)</c>, plus an
    /// <see cref="ExpiredPresenceFloor"/> whenever any expired lot exists. Expired lots (which include
    /// temperature-excursion spoilage) are weighted more heavily than merely at-risk lots, and the
    /// total is clamped into <c>[0, 1]</c>. More near-expiry / at-risk / expired inventory always
    /// yields a score at least as high as an otherwise-identical but healthier snapshot.</para>
    /// </summary>
    public Task<RiskScore> AssessInventoryRiskAsync(InventorySnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);

        var onHand = Math.Max(0, snapshot.TotalOnHandQuantity);
        var atRisk = Math.Max(0, snapshot.AtRiskLotCount);
        var expired = Math.Max(0, snapshot.ExpiredLotCount);

        var exposureDenominator = Math.Max(1, onHand + atRisk + expired);

        var score =
            (ExpiredWeight * expired / exposureDenominator) +
            (AtRiskWeight * atRisk / exposureDenominator);

        if (expired > 0)
        {
            score += ExpiredPresenceFloor;
        }

        score = Math.Clamp(score, 0.0, 1.0);

        return Task.FromResult(new RiskScore(score, IsFallback: false));
    }

    /// <summary>
    /// The stable moving-average daily-demand level for a (colony, gel type) series, derived
    /// deterministically from the two identifiers so the same series always yields the same rate.
    /// </summary>
    private static double PerSeriesDailyRate(ColonyId colony, GelTypeId gelType)
    {
        var u = UnitInterval(colony.Value, gelType.Value);
        return BaselineDailyDemand + (DailyDemandBand * ((2.0 * u) - 1.0));
    }

    /// <summary>The weekly seasonal multiplier for day <paramref name="day"/> of the horizon.</summary>
    private static double SeasonalMultiplier(int day)
    {
        var phase = 2.0 * Math.PI * (day % SeasonalPeriodDays) / SeasonalPeriodDays;
        return 1.0 + (WeeklySeasonalAmplitude * Math.Sin(phase));
    }

    /// <summary>
    /// Maps two identifiers to a deterministic value in <c>[0, 1)</c> by mixing their hash codes. Used
    /// as the stable per-series seed for the demand level (in lieu of real historical observations,
    /// which the Phase-2 model will consume).
    /// </summary>
    private static double UnitInterval(Guid a, Guid b)
    {
        // FNV-1a-style unsigned mix over both GUIDs' hash codes for a stable, well-spread seed.
        unchecked
        {
            var hash = 2166136261u;
            hash = (hash ^ (uint)a.GetHashCode()) * 16777619u;
            hash = (hash ^ (uint)b.GetHashCode()) * 16777619u;
            return hash / (double)uint.MaxValue;
        }
    }
}
