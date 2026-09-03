using Forge.Application.Forecasting;
using Forge.Domain.Common;
using Forge.Infrastructure.Ml;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit and property tests for the Phase-1 <see cref="StatisticalBaselinePredictor"/> (task 31.2): a
/// non-fallback moving-average / seasonal demand forecast over a horizon that is deterministic for
/// identical inputs, an expiry + temperature-excursion risk score that rises with more near-expiry /
/// at-risk / expired inventory, and an always-available predictor in Phase 1.
/// Validates: Requirements 21.1, 21.3, 21.4.
/// </summary>
public sealed class StatisticalBaselinePredictorTests
{
    private static readonly ColonyId Colony = ColonyId.New();
    private static readonly GelTypeId GelType = GelTypeId.New();

    private static StatisticalBaselinePredictor Predictor() => new();

    // ---- Availability (Req 21.1) ----

    [Fact]
    public void IsAvailable_is_true_in_phase_1()
    {
        Assert.True(Predictor().IsAvailable);
    }

    // ---- Demand forecast (Req 21.3) ----

    [Fact]
    public async Task ForecastDemand_returns_non_fallback_positive_demand_over_horizon()
    {
        var forecast = await Predictor().ForecastDemandAsync(
            Colony, GelType, TimeSpan.FromDays(7), CancellationToken.None);

        Assert.False(forecast.IsFallback);
        Assert.Equal(Colony, forecast.Colony);
        Assert.Equal(GelType, forecast.GelType);
        Assert.Equal(TimeSpan.FromDays(7), forecast.Horizon);
        Assert.True(forecast.ExpectedDemand > 0.0, "a 7-day horizon should forecast positive demand");
    }

    [Fact]
    public async Task ForecastDemand_is_deterministic_for_identical_inputs()
    {
        var predictor = Predictor();
        var horizon = TimeSpan.FromDays(5);

        var first = await predictor.ForecastDemandAsync(Colony, GelType, horizon, CancellationToken.None);
        var second = await predictor.ForecastDemandAsync(Colony, GelType, horizon, CancellationToken.None);

        Assert.Equal(first.ExpectedDemand, second.ExpectedDemand);
    }

    [Fact]
    public async Task ForecastDemand_differs_across_gel_types_so_it_is_not_a_constant()
    {
        var predictor = Predictor();
        var horizon = TimeSpan.FromDays(10);

        var a = await predictor.ForecastDemandAsync(Colony, GelTypeId.New(), horizon, CancellationToken.None);
        var b = await predictor.ForecastDemandAsync(Colony, GelTypeId.New(), horizon, CancellationToken.None);
        var c = await predictor.ForecastDemandAsync(Colony, GelTypeId.New(), horizon, CancellationToken.None);

        // At least one series should differ from the others: the baseline is per-series, not constant.
        Assert.False(a.ExpectedDemand == b.ExpectedDemand && b.ExpectedDemand == c.ExpectedDemand);
    }

    [Fact]
    public async Task ForecastDemand_grows_with_a_longer_horizon()
    {
        var predictor = Predictor();

        var shorter = await predictor.ForecastDemandAsync(Colony, GelType, TimeSpan.FromDays(2), CancellationToken.None);
        var longer = await predictor.ForecastDemandAsync(Colony, GelType, TimeSpan.FromDays(14), CancellationToken.None);

        Assert.True(longer.ExpectedDemand > shorter.ExpectedDemand);
    }

    [Fact]
    public async Task ForecastDemand_over_zero_horizon_is_zero()
    {
        var forecast = await Predictor().ForecastDemandAsync(
            Colony, GelType, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(0.0, forecast.ExpectedDemand);
        Assert.False(forecast.IsFallback);
    }

    // ---- Inventory risk (Req 21.4) ----

    [Fact]
    public async Task AssessRisk_returns_non_fallback_score_in_unit_range()
    {
        var snapshot = new InventorySnapshot(
            AsOf: DateTimeOffset.UnixEpoch,
            TotalOnHandQuantity: 100,
            AtRiskLotCount: 3,
            ExpiredLotCount: 1);

        var risk = await Predictor().AssessInventoryRiskAsync(snapshot, CancellationToken.None);

        Assert.False(risk.IsFallback);
        Assert.InRange(risk.Score, 0.0, 1.0);
    }

    [Fact]
    public async Task AssessRisk_rises_with_more_at_risk_inventory()
    {
        var predictor = Predictor();
        var healthier = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 100, AtRiskLotCount: 1, ExpiredLotCount: 0);
        var riskier = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 100, AtRiskLotCount: 20, ExpiredLotCount: 0);

        var low = await predictor.AssessInventoryRiskAsync(healthier, CancellationToken.None);
        var high = await predictor.AssessInventoryRiskAsync(riskier, CancellationToken.None);

        Assert.True(high.Score > low.Score);
    }

    [Fact]
    public async Task AssessRisk_rises_with_more_expired_inventory()
    {
        var predictor = Predictor();
        var healthier = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 100, AtRiskLotCount: 0, ExpiredLotCount: 0);
        var riskier = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 100, AtRiskLotCount: 0, ExpiredLotCount: 10);

        var low = await predictor.AssessInventoryRiskAsync(healthier, CancellationToken.None);
        var high = await predictor.AssessInventoryRiskAsync(riskier, CancellationToken.None);

        Assert.True(high.Score > low.Score);
    }

    [Fact]
    public async Task AssessRisk_weights_expired_more_than_equal_at_risk_exposure()
    {
        var predictor = Predictor();
        var atRiskOnly = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 50, AtRiskLotCount: 10, ExpiredLotCount: 0);
        var expiredOnly = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 50, AtRiskLotCount: 0, ExpiredLotCount: 10);

        var atRisk = await predictor.AssessInventoryRiskAsync(atRiskOnly, CancellationToken.None);
        var expired = await predictor.AssessInventoryRiskAsync(expiredOnly, CancellationToken.None);

        Assert.True(expired.Score > atRisk.Score);
    }

    [Fact]
    public async Task AssessRisk_of_all_healthy_on_hand_is_negligible()
    {
        var snapshot = new InventorySnapshot(DateTimeOffset.UnixEpoch, TotalOnHandQuantity: 500, AtRiskLotCount: 0, ExpiredLotCount: 0);

        var risk = await Predictor().AssessInventoryRiskAsync(snapshot, CancellationToken.None);

        Assert.Equal(0.0, risk.Score);
    }
}
