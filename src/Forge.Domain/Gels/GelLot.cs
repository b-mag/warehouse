using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Events;

namespace Forge.Domain.Gels;

/// <summary>
/// A produced batch of a single <see cref="GelType"/> (Req 3.1). A lot tracks its formulation
/// reference (via <see cref="GelTypeId"/>), when it was produced and when it expires, its on-hand
/// quantity, a FEFO tie-break priority, its temperature history, and the flags later rules use to
/// exclude it from selection (<see cref="IsExpired"/>) or mark it compromised (<see cref="AtRisk"/>).
/// <para>
/// <b>Expiry derivation.</b> On creation the expiry timestamp is derived from the production
/// timestamp and the formulation's nominal shelf-life: <c>ExpiresAt = ProducedAt + NominalShelfLife</c>
/// (Req 3.4, 11.4). Use <see cref="Create(GelLotId, GelType, DateTimeOffset, int, int, ZoneId?)"/>
/// (or the formulation overload) so callers never compute expiry themselves.
/// </para>
/// <para>
/// <b>Scope.</b> This type establishes the data shape plus the pure, deterministic
/// <see cref="RemainingShelfLife(DateTimeOffset)"/> query (Req 3.5, 4.7). The temperature-reading
/// cold-chain rule that appends to history in timestamp order / detects excursions / toggles
/// <see cref="AtRisk"/> is implemented as the pure
/// <see cref="RecordTemperature(TemperatureReading, TemperatureRange, out Events.TemperatureExcursion?)"/>
/// method (task 24.3), invoked by the <c>RecordTemperatureReading</c> handler which supplies the
/// assigned zone's allowable range. The mutable fields are private-set so only in-domain rules can
/// change them.
/// </para>
/// <para>
/// <b>Expiry decay (task 5.1).</b> The deterministic Expiry_Decay rule is implemented as a method on
/// this lot: <see cref="TryExpireAt(DateTimeOffset, out LotExpired?)"/>. It is a per-lot transition the
/// per-tick pipeline (task 24.4) invokes; the pipeline supplies the current time, so this rule performs
/// no clock access and no RNG and is a pure function of <see cref="ExpiresAt"/> and the passed
/// <c>now</c> (Req 4.7). Remaining shelf-life is measured in <em>whole seconds</em>
/// (<see cref="RemainingWholeSeconds(DateTimeOffset)"/>, Req 4.1); a lot transitions to expired exactly
/// when that value is at or below zero (Req 4.3).
/// </para>
/// </summary>
public sealed class GelLot
{
    private readonly List<TemperatureReading> _history = [];

    private GelLot(
        GelLotId id,
        GelTypeId gelTypeId,
        DateTimeOffset producedAt,
        DateTimeOffset expiresAt,
        int quantity,
        int fefoPriority,
        ZoneId? assignedZoneId)
    {
        Id = id;
        GelTypeId = gelTypeId;
        ProducedAt = producedAt;
        ExpiresAt = expiresAt;
        Quantity = quantity;
        FefoPriority = fefoPriority;
        AssignedZoneId = assignedZoneId;
    }

    /// <summary>The strongly-typed identity of this lot (Req 3.1); also the final FEFO tie-break (Req 5.2).</summary>
    public GelLotId Id { get; }

    /// <summary>The gel type / formulation family this lot belongs to (Req 3.1).</summary>
    public GelTypeId GelTypeId { get; }

    /// <summary>When the lot was produced (Req 3.1).</summary>
    public DateTimeOffset ProducedAt { get; }

    /// <summary>When the lot expires; derived on creation as <c>ProducedAt + NominalShelfLife</c> (Req 3.4, 11.4).</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>On-hand quantity (Req 3.1). Mutated only by in-domain rules (fulfillment/put-away, later tasks).</summary>
    public int Quantity { get; private set; }

    /// <summary>FEFO tie-break key applied after expiry, before lot id (Req 5.2).</summary>
    public int FefoPriority { get; }

    /// <summary>
    /// Whether the lot has been marked expired. Set only by the expiry-decay rule (task 5) on the
    /// non-expired→expired transition (Req 4.3, 4.6); expired lots are excluded from FEFO (Req 4.5).
    /// </summary>
    public bool IsExpired { get; private set; }

    /// <summary>The zone the lot is stored in, if any. A zone-less lot cannot record temperature readings (Req 6.4).</summary>
    public ZoneId? AssignedZoneId { get; private set; }

    /// <summary>Whether the lot has an unresolved temperature excursion (Req 6.5). Toggled by the cold-chain rule (task 7).</summary>
    public bool AtRisk { get; private set; }

    /// <summary>Temperature readings in timestamp order (Req 3.1). Appended by the cold-chain rule (tasks 7 / 24.3).</summary>
    public IReadOnlyList<TemperatureReading> TemperatureHistory => _history;

    /// <summary>
    /// Create a lot, deriving <see cref="ExpiresAt"/> from the gel type's formulation nominal
    /// shelf-life: <c>ExpiresAt = producedAt + gelType.Formulation.NominalShelfLife</c> (Req 3.4, 11.4).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="gelType"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantity"/> is negative.</exception>
    public static GelLot Create(
        GelLotId id,
        GelType gelType,
        DateTimeOffset producedAt,
        int quantity,
        int fefoPriority = 0,
        ZoneId? assignedZoneId = null)
    {
        ArgumentNullException.ThrowIfNull(gelType);
        return Create(id, gelType.Id, gelType.Formulation, producedAt, quantity, fefoPriority, assignedZoneId);
    }

    /// <summary>
    /// Create a lot from an explicit gel-type id + formulation, deriving <see cref="ExpiresAt"/> as
    /// <c>producedAt + formulation.NominalShelfLife</c> (Req 3.4, 11.4). Useful when the caller holds
    /// the formulation without the full <see cref="GelType"/> instance.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="formulation"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="quantity"/> is negative.</exception>
    public static GelLot Create(
        GelLotId id,
        GelTypeId gelTypeId,
        Formulation formulation,
        DateTimeOffset producedAt,
        int quantity,
        int fefoPriority = 0,
        ZoneId? assignedZoneId = null)
    {
        ArgumentNullException.ThrowIfNull(formulation);
        ArgumentOutOfRangeException.ThrowIfNegative(quantity);

        var expiresAt = producedAt + formulation.NominalShelfLife;
        return new GelLot(id, gelTypeId, producedAt, expiresAt, quantity, fefoPriority, assignedZoneId);
    }

    /// <summary>
    /// The remaining shelf-life as the signed duration between <paramref name="now"/> and expiry:
    /// <c>ExpiresAt - now</c> (Req 3.5). Pure and deterministic — identical <see cref="ExpiresAt"/>
    /// and identical <paramref name="now"/> always yield the identical value (Req 4.7). The result
    /// is negative once <paramref name="now"/> is past expiry; interpreting that as "expired" is the
    /// expiry-decay rule's job (task 5), not this query's.
    /// </summary>
    public TimeSpan RemainingShelfLife(DateTimeOffset now) => ExpiresAt - now;

    /// <summary>
    /// The remaining shelf-life measured in <b>whole seconds</b> (Req 4.1): the signed number of whole
    /// seconds between <paramref name="now"/> and <see cref="ExpiresAt"/>, truncated toward zero so any
    /// sub-second remainder does not extend or shorten the count. This is the quantity the Expiry_Decay
    /// rule tests: a lot is expired exactly when this value is at or below zero (Req 4.3). Pure and
    /// deterministic in (<see cref="ExpiresAt"/>, <paramref name="now"/>) (Req 4.7).
    /// </summary>
    public long RemainingWholeSeconds(DateTimeOffset now) =>
        (long)RemainingShelfLife(now).TotalSeconds;

    /// <summary>
    /// Apply the deterministic Expiry_Decay rule for the current simulated time <paramref name="now"/>
    /// (Req 4). Because <see cref="ExpiresAt"/> is fixed at creation, "reducing" remaining shelf-life by
    /// an advance is equivalent to recomputing it against <paramref name="now"/>; evaluating at a later
    /// <paramref name="now"/> yields a proportionally smaller <see cref="RemainingWholeSeconds(DateTimeOffset)"/>
    /// (Req 4.1), and evaluating at an unchanged <paramref name="now"/> leaves it identical (Req 4.2, 4.7).
    /// <list type="bullet">
    /// <item>If the lot is already expired, nothing changes and no event is raised — idempotent (Req 4.4).</item>
    /// <item>If <see cref="RemainingWholeSeconds(DateTimeOffset)"/> is at or below zero, the lot transitions
    /// to <see cref="IsExpired"/> and exactly one <see cref="LotExpired"/> event identifying this lot is
    /// returned via <paramref name="expiredEvent"/> (Req 4.3, 4.6). Otherwise it stays non-expired.</item>
    /// </list>
    /// The rule performs no clock access and no randomness: the caller (the per-tick pipeline, task 24.4)
    /// supplies <paramref name="now"/>, keeping the transition a pure function of (<see cref="ExpiresAt"/>,
    /// <paramref name="now"/>) (Req 4.7).
    /// </summary>
    /// <param name="now">The current simulated time to evaluate expiry against.</param>
    /// <param name="expiredEvent">
    /// The single <see cref="LotExpired"/> event on a fresh non-expired→expired transition; otherwise <c>null</c>.
    /// </param>
    /// <returns><c>true</c> iff this call transitioned the lot from non-expired to expired.</returns>
    public bool TryExpireAt(DateTimeOffset now, out LotExpired? expiredEvent)
    {
        expiredEvent = null;

        // Already expired: idempotent no-op, no further reduction, no event (Req 4.4).
        if (IsExpired)
        {
            return false;
        }

        // Still has whole-second shelf-life remaining: unchanged, no event (Req 4.2 for a
        // non-advancing/insufficient now, Req 4.3 boundary is "at or below zero").
        if (RemainingWholeSeconds(now) > 0)
        {
            return false;
        }

        // Non-expired -> expired transition: mark and raise exactly one identifying event (Req 4.3, 4.6).
        IsExpired = true;
        expiredEvent = new LotExpired(Id, now);
        return true;
    }

    /// <summary>
    /// Record a temperature <paramref name="reading"/> against this lot's assigned cold-chain zone
    /// (Req 6.2, 6.3, 6.4). This is the pure, deterministic cold-chain rule the
    /// <c>RecordTemperatureReading</c> handler (task 24.3) invokes after loading the lot and the zone
    /// it is stored in; the handler supplies the assigned zone's <paramref name="allowableRange"/> so
    /// this method performs no repository access, no clock access, and no randomness.
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Zone-less rejection (Req 6.4).</b> A lot with no <see cref="AssignedZoneId"/> cannot record
    ///     a reading: the call is rejected with <see cref="DomainError.NoAssignedZone(string)"/> and the
    ///     lot is left completely unchanged (no history append, no flag change).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Timestamp-ordered append (Req 6.2).</b> On success the reading is inserted into
    ///     <see cref="TemperatureHistory"/> so the history stays ordered by
    ///     <see cref="TemperatureReading.At"/> ascending, regardless of the order readings arrive in.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Excursion detection + at-risk (Req 6.3).</b> The reading is an excursion iff it falls
    ///     outside the inclusive <paramref name="allowableRange"/>
    ///     (<see cref="TemperatureRange.IsExcursion(decimal)"/>). On an excursion the lot is flagged
    ///     <see cref="AtRisk"/> and the method reports <c>excursionDetected = true</c> via
    ///     <paramref name="excursion"/> so the handler can raise the <see cref="TemperatureExcursion"/>
    ///     event; an in-range reading never clears an already-set <see cref="AtRisk"/> flag (an
    ///     excursion stays unresolved until a separate resolution step, Req 6.5).
    ///   </description></item>
    /// </list>
    /// Detection is a pure function of (<paramref name="reading"/>, <paramref name="allowableRange"/>),
    /// keeping it deterministic (Req 6.6).
    /// </summary>
    /// <param name="reading">The recorded temperature value + timestamp to append.</param>
    /// <param name="allowableRange">The assigned zone's inclusive allowable temperature band.</param>
    /// <param name="excursion">
    /// On success, the <see cref="TemperatureExcursion"/> event identifying this lot + reading when the
    /// reading is an excursion; otherwise <c>null</c>. Undefined on a failed result.
    /// </param>
    /// <returns>
    /// <see cref="Result.Success()"/> when the reading was recorded (whether or not it was an excursion);
    /// a <see cref="DomainError.NoAssignedZone(string)"/> failure when the lot has no assigned zone.
    /// </returns>
    public Result RecordTemperature(
        TemperatureReading reading,
        TemperatureRange allowableRange,
        out TemperatureExcursion? excursion)
    {
        ArgumentNullException.ThrowIfNull(reading);
        excursion = null;

        // Req 6.4: a zone-less lot cannot record a reading; reject leaving state unchanged.
        if (AssignedZoneId is null)
        {
            return DomainError.NoAssignedZone(
                $"Gel lot {Id} has no assigned zone; a temperature reading cannot be recorded against it.");
        }

        // Req 6.2: insert keeping the history ordered ascending by timestamp. Find the first existing
        // reading strictly later than the incoming one and insert before it (stable for equal timestamps,
        // which preserves arrival order among ties).
        var insertAt = _history.Count;
        for (var i = 0; i < _history.Count; i++)
        {
            if (_history[i].At > reading.At)
            {
                insertAt = i;
                break;
            }
        }

        _history.Insert(insertAt, reading);

        // Req 6.3: excursion iff the value falls outside the inclusive allowable band. Flag the lot
        // at-risk and surface the event for the handler to publish. In-range readings never clear an
        // already-unresolved excursion (Req 6.5).
        if (allowableRange.IsExcursion(reading.Celsius))
        {
            AtRisk = true;
            excursion = new TemperatureExcursion(Id, reading.Celsius, reading.At);
        }

        return Result.Success();
    }
}
