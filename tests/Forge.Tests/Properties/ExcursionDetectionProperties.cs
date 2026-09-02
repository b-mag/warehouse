using CsCheck;
using Forge.Domain.ColdChain;

// Feature: nutrient-forge, Property 3: Temperature-excursion detection is deterministic with inclusive bounds
namespace Forge.Tests.Properties;

/// <summary>
/// Property 3 (design): Temperature-excursion detection is deterministic with inclusive bounds
/// (task 7.2). For any temperature reading value and any zone allowable range, an excursion is
/// detected if and only if the value is strictly below the inclusive minimum or strictly above
/// the inclusive maximum, and identical inputs always yield an identical outcome.
///
/// Validates: Requirements 6.3, 6.6.
///
/// These property tests exercise the pure <see cref="TemperatureRange.IsExcursion"/> primitive —
/// the deterministic function of (range, value) that the RecordTemperatureReading handler
/// (task 24.3) later uses against a lot's assigned zone's allowable range.
/// </summary>
public sealed class ExcursionDetectionProperties
{
    /// <summary>Number of generated cases per property (design mandates >= 100 iterations).</summary>
    private const int Iterations = 100;

    /// <summary>
    /// Generates a valid <see cref="TemperatureRange"/> (Min &lt;= Max) by sorting a pair of
    /// decimals, together with an independent reading value drawn from a range that overlaps and
    /// extends beyond the band so that in-band, exactly-on-bound, and out-of-band values all occur.
    /// </summary>
    private static readonly Gen<(TemperatureRange Range, decimal Value)> RangeAndValue =
        Gen.Select(
            Gen.Decimal[-300m, 300m],
            Gen.Decimal[-300m, 300m],
            Gen.Decimal[-400m, 400m],
            (a, b, value) =>
            {
                var min = Math.Min(a, b);
                var max = Math.Max(a, b);
                return (new TemperatureRange(min, max), value);
            });

    [Fact]
    public void Excursion_IsTrue_IffValueOutsideInclusiveBounds()
    {
        RangeAndValue.Sample(
            input =>
            {
                var (range, value) = input;

                var expectedExcursion = value < range.MinCelsius || value > range.MaxCelsius;

                // IsExcursion is TRUE exactly when the value is below Min or above Max, and FALSE
                // exactly on the inclusive interval [Min, Max].
                return range.IsExcursion(value) == expectedExcursion;
            },
            iter: Iterations);
    }

    [Fact]
    public void Excursion_IsExactNegationOfContains()
    {
        RangeAndValue.Sample(
            input =>
            {
                var (range, value) = input;

                // The inclusive-bounds semantics live in one place: excursion is the negation of Contains.
                return range.IsExcursion(value) == !range.Contains(value);
            },
            iter: Iterations);
    }

    [Fact]
    public void Excursion_IsFalse_ExactlyOnInclusiveInterval()
    {
        // For any valid range, both endpoints and any interior in-band value are NOT excursions.
        // Bounds and the interior probe are drawn from an integer temperature grid so every probe
        // is an EXACT decimal provably within [Min, Max] — no floating-point interpolation error.
        Gen.SelectMany(
                Gen.Select(Gen.Int[-300, 300], Gen.Int[-300, 300], (a, b) => (Min: Math.Min(a, b), Max: Math.Max(a, b))),
                bounds => Gen.Int[bounds.Min, bounds.Max]
                    .Select(k =>
                    {
                        var min = (decimal)bounds.Min;
                        var max = (decimal)bounds.Max;
                        return (Range: new TemperatureRange(min, max), Min: min, Max: max, InBand: (decimal)k);
                    }))
            .Sample(
                input =>
                    !input.Range.IsExcursion(input.Min)
                    && !input.Range.IsExcursion(input.Max)
                    && !input.Range.IsExcursion(input.InBand),
                iter: Iterations);
    }

    [Fact]
    public void Excursion_IsDeterministic_ForIdenticalInputs()
    {
        RangeAndValue.Sample(
            input =>
            {
                var (range, value) = input;

                // An identical (range, value) pair must always yield an identical outcome. We use a
                // structurally-equal copy of the range to confirm the result depends only on the
                // value equality of the inputs, not on reference identity.
                var rangeCopy = new TemperatureRange(range.MinCelsius, range.MaxCelsius);

                var first = range.IsExcursion(value);
                var second = range.IsExcursion(value);
                var onCopy = rangeCopy.IsExcursion(value);

                return first == second && first == onCopy;
            },
            iter: Iterations);
    }
}
