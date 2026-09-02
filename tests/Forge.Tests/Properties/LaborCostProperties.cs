using CsCheck;

using Forge.Application.Labor;

using Forge.Domain.Common;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 7: Labor-cost consistency
//
// Property 7 (design.md): "For any task duration, travel time, and worker hourly rate, the accrued
// labor cost SHALL equal (duration + travel time) × hourly rate, and any two tasks with identical
// duration, identical travel time, and identical hourly rate SHALL accrue an identical labor cost."
//
// Validates: Requirements 15.3, 15.9, 28.8
//
// The System Under Test is the Application labor-cost arithmetic (task 19.1): the pure
// LaborCostCalculator.ComputeLaborCost and the LaborLedger that accrues it on completion. Both compute
// entirely in decimal from the integral TimeSpan tick counts, so the cost is exact and reproducible.
public sealed class LaborCostProperties
{
    // >=100 iterations required by the spec.
    private const int Iterations = 100;

    // Durations and travel times as whole seconds in a wide but bounded range: from zero (a free /
    // co-located task) up to eight hours, so both the zero-cost edge and multi-hour accruals are hit.
    private static readonly Gen<TimeSpan> GenSpan =
        Gen.Int[0, 8 * 60 * 60].Select(seconds => TimeSpan.FromSeconds(seconds));

    // Hourly rates as exact decimals with cents precision, including zero (an unpaid role). Generated
    // from an integer count of cents so the generator itself introduces no floating-point noise.
    private static readonly Gen<decimal> GenRate =
        Gen.Int[0, 500_00].Select(cents => cents / 100m);

    // The reference cost computed independently of the SUT, using the same exact decimal, ticks-based
    // definition the requirement states: (duration + travel) hours × rate. This is the ground truth the
    // property is checked against, not a re-call of the SUT.
    private static decimal ReferenceCost(TimeSpan duration, TimeSpan travel, decimal rate)
    {
        if (rate <= 0m)
        {
            return 0m;
        }

        long totalTicks = duration.Ticks + travel.Ticks;
        if (totalTicks <= 0L)
        {
            return 0m;
        }

        decimal hours = (decimal)totalTicks / TimeSpan.TicksPerHour;
        return hours * rate;
    }

    // Req 15.3 / 15.9: the accrued cost equals (duration + travel) hours × rate, exactly.
    [Fact]
    public void AccruedCostEqualsDurationPlusTravelHoursTimesRate()
    {
        Gen.Select(GenSpan, GenSpan, GenRate)
            .Sample((duration, travel, rate) =>
            {
                var expected = ReferenceCost(duration, travel, rate);

                // Pure calculator.
                var computed = LaborCostCalculator.ComputeLaborCost(duration, travel, rate);
                Assert.Equal(expected, computed);

                // Ledger accrual on completion accrues the same exact amount and reflects it in the total.
                var ledger = new LaborLedger();
                var worker = WorkerId.New();
                var accrued = ledger.AccrueOnCompletion(worker, duration, travel, rate);

                Assert.Equal(expected, accrued);
                Assert.Equal(expected, ledger.TotalLaborCost);
                Assert.Equal(expected, ledger.UtilizationFor(worker).LaborCost);
            }, iter: Iterations);
    }

    // Req 15.9 / 28.8: two tasks with identical (duration, travel, rate) accrue an identical cost —
    // accrual is deterministic given identical inputs, regardless of which worker completes each.
    [Fact]
    public void IdenticalInputsAccrueIdenticalCost()
    {
        Gen.Select(GenSpan, GenSpan, GenRate)
            .Sample((duration, travel, rate) =>
            {
                var costA = LaborCostCalculator.ComputeLaborCost(duration, travel, rate);
                var costB = LaborCostCalculator.ComputeLaborCost(duration, travel, rate);
                Assert.Equal(costA, costB);

                // Accrue each identical task to a different worker; each worker's accrued cost matches.
                var ledger = new LaborLedger();
                var workerA = WorkerId.New();
                var workerB = WorkerId.New();

                var accruedA = ledger.AccrueOnCompletion(workerA, duration, travel, rate);
                var accruedB = ledger.AccrueOnCompletion(workerB, duration, travel, rate);

                Assert.Equal(accruedA, accruedB);
                Assert.Equal(ledger.UtilizationFor(workerA).LaborCost, ledger.UtilizationFor(workerB).LaborCost);

                // The total is the exact sum of the two identical accruals (no drift on summation).
                Assert.Equal(accruedA + accruedB, ledger.TotalLaborCost);
            }, iter: Iterations);
    }
}
