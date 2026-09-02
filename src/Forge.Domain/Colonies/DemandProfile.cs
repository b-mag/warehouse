namespace Forge.Domain.Colonies;

using Forge.Domain.Common;

/// <summary>
/// A colony's demand shape as <b>pure data</b> (Req 12.1, 12.3, 12.6). It describes how much of
/// each gel type a colony consumes per hour at baseline (<see cref="BaseRatePerHour"/>) and how
/// that baseline evolves over simulated time via ordered <see cref="Trends"/> (trend boundaries).
/// <para>
/// This type contains <b>no demand generator</b>. Authoritative colony-demand generation lives in
/// <c>Forge.Simulation.ColonyDemandSimulator</c> (task 27); this record is the data that simulator
/// consumes (Req 1.8 — the WMS Core holds no input-generation logic). The only behavior here is a
/// pure, deterministic <em>selection</em> helper (<see cref="MultiplierAt"/> / <see cref="RateAt"/>)
/// that reports which pattern is active at a given simulated time — it never produces orders.
/// </para>
/// <para>
/// <b>Equality.</b> A positional record's synthesized equality compares the
/// <see cref="BaseRatePerHour"/> dictionary and <see cref="Trends"/> list by <em>reference</em>,
/// so two profiles built from equal-but-distinct collections would compare unequal. That is wrong
/// for the distinctness checks the seeder relies on (Req 25.3). This record therefore overrides
/// <see cref="Equals(DemandProfile)"/> and <see cref="GetHashCode"/> to compare by <em>content</em>:
/// the base-rate maps are equal when they contain the same key/value pairs (order-independent), and
/// the trend lists are equal when they contain the same boundaries in the same order. As a result,
/// two profiles are unequal whenever they differ in at least one attribute, and equal exactly when
/// every attribute matches.
/// </para>
/// </summary>
public sealed record DemandProfile
{
    // Private ctor: instances are only ever produced through the validated Create factory, and the
    // stored collections are defensively copied there so the invariants below always hold:
    //   - BaseRatePerHour holds only non-negative, finite rates (Req 12.6);
    //   - Trends is ordered ascending by StartsAt with only valid boundaries (Req 12.3, 12.6).
    private DemandProfile(
        IReadOnlyDictionary<GelTypeId, double> baseRatePerHour,
        IReadOnlyList<TrendBoundary> trends)
    {
        BaseRatePerHour = baseRatePerHour;
        Trends = trends;
    }

    /// <summary>Baseline consumption rate (units/hour) per gel type; every value is non-negative and finite.</summary>
    public IReadOnlyDictionary<GelTypeId, double> BaseRatePerHour { get; }

    /// <summary>
    /// Trend boundaries that evolve consumption over simulated time (Req 12.3), stored ascending by
    /// <see cref="TrendBoundary.StartsAt"/>. May be empty (constant baseline).
    /// </summary>
    public IReadOnlyList<TrendBoundary> Trends { get; }

    /// <summary>
    /// Validate profile attributes (Req 12.6) and build a profile with defensively-copied,
    /// normalized collections.
    /// <list type="bullet">
    /// <item>Rejects a null collection, naming the parameter.</item>
    /// <item>Rejects any base rate that is NaN, infinite, or negative, naming the offending gel type.</item>
    /// <item>Rejects any malformed trend boundary (NaN/infinite/negative multiplier), naming the attribute.</item>
    /// </list>
    /// On success the returned profile stores an independent snapshot (callers cannot mutate the
    /// internals afterward) and the trends are sorted ascending by start time so
    /// <see cref="MultiplierAt"/> is a simple scan.
    /// </summary>
    public static Result<DemandProfile> Create(
        IReadOnlyDictionary<GelTypeId, double> baseRatePerHour,
        IReadOnlyList<TrendBoundary> trends)
    {
        if (baseRatePerHour is null)
        {
            return DomainError.Validation(
                "Demand profile base rates are required.", nameof(baseRatePerHour));
        }

        if (trends is null)
        {
            return DomainError.Validation(
                "Demand profile trend boundaries are required.", nameof(trends));
        }

        foreach (var (gelType, rate) in baseRatePerHour)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate))
            {
                return DomainError.Validation(
                    $"Base rate for gel type {gelType} must be a finite number but was {rate}.",
                    nameof(BaseRatePerHour));
            }

            if (rate < 0)
            {
                return DomainError.Validation(
                    $"Base rate for gel type {gelType} must be non-negative but was {rate}.",
                    nameof(BaseRatePerHour));
            }
        }

        // Re-validate each boundary through its own factory so the range rules live in one place.
        foreach (var trend in trends)
        {
            if (trend is null)
            {
                return DomainError.Validation(
                    "Trend boundaries must not contain null entries.", nameof(Trends));
            }

            var boundary = TrendBoundary.Create(trend.StartsAt, trend.Multiplier);
            if (boundary.IsFailure)
            {
                return Result<DemandProfile>.Failure(boundary.Error);
            }
        }

        // Defensive copies: an independent dictionary and a stably-sorted list (ascending start).
        var rates = new Dictionary<GelTypeId, double>(baseRatePerHour);
        var orderedTrends = trends
            .OrderBy(t => t.StartsAt)
            .ToArray();

        return new DemandProfile(rates, orderedTrends);
    }

    /// <summary>
    /// Pure, deterministic selection: the consumption multiplier active at simulated time
    /// <paramref name="at"/> (Req 12.3). Returns the <see cref="TrendBoundary.Multiplier"/> of the
    /// latest boundary whose start is at or before <paramref name="at"/>, or <c>1.0</c> (baseline)
    /// when no boundary has started yet. Does not generate orders.
    /// </summary>
    public double MultiplierAt(DateTimeOffset at)
    {
        // Trends are sorted ascending; scan for the last boundary that has started.
        var multiplier = 1.0;
        foreach (var trend in Trends)
        {
            if (trend.StartsAt <= at)
            {
                multiplier = trend.Multiplier;
            }
            else
            {
                break;
            }
        }

        return multiplier;
    }

    /// <summary>
    /// Pure, deterministic selection: the effective consumption rate (units/hour) for
    /// <paramref name="gelType"/> at simulated time <paramref name="at"/> — the base rate scaled by
    /// the active trend multiplier (Req 12.3). Returns <c>0.0</c> for a gel type the profile does not
    /// mention. Does not generate orders.
    /// </summary>
    public double RateAt(GelTypeId gelType, DateTimeOffset at)
    {
        var baseRate = BaseRatePerHour.TryGetValue(gelType, out var r) ? r : 0.0;
        return baseRate * MultiplierAt(at);
    }

    /// <summary>
    /// Content-based equality (see the type remarks). Two profiles are equal when their base-rate
    /// maps hold the same key/value pairs and their trend lists hold the same boundaries in the same
    /// order.
    /// </summary>
    public bool Equals(DemandProfile? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return BaseRatesEqual(BaseRatePerHour, other.BaseRatePerHour)
            && Trends.SequenceEqual(other.Trends);
    }

    /// <summary>Hash code consistent with the content-based <see cref="Equals(DemandProfile)"/>.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();

        // Order-independent contribution from the base-rate map: combine per-entry hashes with an
        // associative/commutative accumulator so key ordering does not affect the result.
        var mapAccumulator = 0;
        foreach (var (key, value) in BaseRatePerHour)
        {
            mapAccumulator += HashCode.Combine(key, value);
        }

        hash.Add(mapAccumulator);

        // Order-sensitive contribution from the trend list.
        hash.Add(Trends.Count);
        foreach (var trend in Trends)
        {
            hash.Add(trend);
        }

        return hash.ToHashCode();
    }

    private static bool BaseRatesEqual(
        IReadOnlyDictionary<GelTypeId, double> left,
        IReadOnlyDictionary<GelTypeId, double> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !value.Equals(other))
            {
                return false;
            }
        }

        return true;
    }
}
