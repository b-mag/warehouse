namespace Forge.Application.Labor;

/// <summary>
/// Pure, deterministic labor-cost arithmetic (Req 15.3, 15.9). Computes
/// <c>Labor_Cost = (EstimatedDuration + TravelTime) × HourlyRate</c> as an exact
/// <see cref="decimal"/> so identical inputs always yield an identical cost (Req 15.9).
/// <para>
/// <b>Why ticks-based, not <c>TimeSpan.TotalHours</c>.</b> <see cref="TimeSpan.TotalHours"/> is a
/// <see cref="double"/>, so converting through it would introduce binary floating-point drift and
/// make the accrued cost sensitive to how the duration was constructed (e.g. <c>0.1h</c> is not
/// exactly representable in <c>double</c>). Money must be exact and reproducible, so this converts
/// the combined duration to hours entirely in <see cref="decimal"/> from the integral
/// <see cref="TimeSpan.Ticks"/> count: <c>hours = totalTicks / TimeSpan.TicksPerHour</c>, where both
/// operands are exact integers lifted to <see cref="decimal"/>. The result is the exact rational
/// value of the tick count in hours (to <see cref="decimal"/> precision), multiplied by the rate.
/// Because every step is <see cref="decimal"/> and driven only by the integral tick counts and the
/// rate, the computation is a total, side-effect-free function of its three inputs — the determinism
/// Req 15.9 requires.
/// </para>
/// <para>
/// The combined duration is treated as non-negative: the domain guarantees both
/// <c>EstimatedDuration &gt;= 0</c> (Req 15.2) and <c>TravelTime &gt;= 0</c> (Req 15.4) through
/// <c>WarehouseTask</c>'s guarded construction/setter, and worker rates are guaranteed
/// <c>&gt;= 0</c> (Req 15.1); this calculator additionally clamps a negative combined duration or a
/// negative rate to zero so a caller can never accrue a negative cost.
/// </para>
/// </summary>
public static class LaborCostCalculator
{
    /// <summary>
    /// Compute the labor cost for a task as <c>(duration + travelTime) hours × hourlyRate</c>,
    /// exactly and deterministically in <see cref="decimal"/> (Req 15.3, 15.9).
    /// </summary>
    /// <param name="duration">The task's estimated work duration (Req 15.2). Non-negative in practice.</param>
    /// <param name="travelTime">The task's derived travel time (Req 15.4). Non-negative in practice.</param>
    /// <param name="hourlyRate">The assigned worker's hourly rate (Req 15.1). Non-negative in practice.</param>
    /// <returns>
    /// The accrued labor cost as an exact <see cref="decimal"/>. A negative combined duration or a
    /// negative rate is clamped to zero, so the result is always <c>&gt;= 0</c>.
    /// </returns>
    public static decimal ComputeLaborCost(TimeSpan duration, TimeSpan travelTime, decimal hourlyRate)
    {
        if (hourlyRate <= 0m)
        {
            return 0m;
        }

        // Combine in ticks (exact integers). Guard against a saturating overflow when either input is
        // TimeSpan.MaxValue (e.g. an unroutable-path travel time): treat that as "infinite" and return
        // the largest representable cost rather than throwing, keeping the method total.
        long totalTicks;
        try
        {
            totalTicks = checked(duration.Ticks + travelTime.Ticks);
        }
        catch (OverflowException)
        {
            return decimal.MaxValue;
        }

        if (totalTicks <= 0L)
        {
            return 0m;
        }

        // hours = totalTicks / TicksPerHour, computed entirely in decimal for an exact rational value.
        decimal hours = (decimal)totalTicks / TimeSpan.TicksPerHour;
        return hours * hourlyRate;
    }
}
