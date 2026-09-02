namespace Forge.Domain.ColdChain;

/// <summary>
/// An allowable storage temperature band with an inclusive minimum and inclusive maximum,
/// in degrees Celsius (Req 6.1).
/// <para>
/// Modeled as a <c>readonly record struct</c> so it is a small, immutable value with
/// structural equality — two ranges are equal exactly when both bounds are equal, which is
/// what the deterministic excursion detection (Req 6.6) relies on. The type is
/// simulation-agnostic and BCL-only; it carries no behavior beyond pure predicates over its
/// two bounds.
/// </para>
/// <para>
/// This type is OWNED by the cold-chain subsystem (task 7.1) and is referenced by the Gels
/// subsystem (a <c>Formulation</c>'s required <c>StorageRange</c>). It is defined here exactly
/// as the design's domain model prescribes.
/// </para>
/// </summary>
/// <param name="MinCelsius">Inclusive lower bound of the allowable band, in Celsius (Req 6.1).</param>
/// <param name="MaxCelsius">Inclusive upper bound of the allowable band, in Celsius (Req 6.1).</param>
public readonly record struct TemperatureRange(decimal MinCelsius, decimal MaxCelsius)
{
    /// <summary>
    /// True when <paramref name="c"/> lies within the band, treating both bounds as inclusive
    /// (Req 6.1). A value exactly equal to <see cref="MinCelsius"/> or <see cref="MaxCelsius"/>
    /// is contained.
    /// </summary>
    public bool Contains(decimal c) => c >= MinCelsius && c <= MaxCelsius;

    /// <summary>
    /// True when <paramref name="r"/> is fully enclosed by this range, i.e. its minimum is at
    /// or above this minimum and its maximum is at or below this maximum. Used for zone/gel
    /// storage-compatibility checks (a zone's allowable range must contain a gel's required
    /// storage range).
    /// </summary>
    public bool ContainsRange(TemperatureRange r) =>
        r.MinCelsius >= MinCelsius && r.MaxCelsius <= MaxCelsius;

    /// <summary>
    /// True when <paramref name="c"/> is a temperature excursion for this range — i.e. it falls
    /// below the inclusive minimum or above the inclusive maximum (Req 6.3). This is a pure,
    /// deterministic function of the reading value and the range only: identical inputs always
    /// yield an identical outcome (Req 6.6). It is exactly the negation of <see cref="Contains"/>,
    /// which keeps the inclusive-bounds semantics of Req 6.1 authoritative in one place.
    /// <para>
    /// This is the primitive the later <c>RecordTemperatureReading</c> handler (task 24.3) uses
    /// against a lot's assigned zone's <see cref="TemperatureZone.AllowableRange"/>; the handler's
    /// history-append / at-risk / event-raise / zone-less-rejection behavior lives there, not here.
    /// </para>
    /// </summary>
    public bool IsExcursion(decimal c) => !Contains(c);
}
