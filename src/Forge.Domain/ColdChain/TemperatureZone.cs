using Forge.Domain.Capacity;
using Forge.Domain.Common;

namespace Forge.Domain.ColdChain;

/// <summary>
/// A defined storage area with an allowable temperature range and a finite storage capacity
/// (Req 6.1). A zone tracks how much is currently stored and exposes its remaining available
/// capacity (Req 7.5).
/// <para>
/// <b>Capacity reconciliation (Req 6.1 vs Req 7.7).</b> Two acceptance criteria constrain
/// capacity from different angles:
/// <list type="bullet">
///   <item><description>Req 6.1 models a zone capacity as a finite value between <b>1 and 100000</b> gel lots.</description></item>
///   <item><description>Req 7.7 says every capacity is treated as <c>&gt;= 0</c> and any <b>negative</b> configuration is rejected with an "invalid capacity" error.</description></item>
/// </list>
/// These are reconciled as follows: the modeled valid range is <b>1..100000</b> inclusive
/// (per Req 6.1). A negative value is rejected with <see cref="ErrorKind.InvalidCapacity"/>
/// (satisfying Req 7.7's explicit negative-rejection), and any value outside 1..100000
/// (including <c>0</c>, which Req 6.1 excludes as a usable storage zone) is likewise rejected
/// with <see cref="ErrorKind.InvalidCapacity"/>. Thus negative rejection (Req 7.7) is a strict
/// subset of the 1..100000 guard (Req 6.1), and construction goes through the validated
/// <see cref="Create"/> factory so an invalid zone can never exist.
/// </para>
/// <para>
/// This type provides the zone model plus the pure range/excursion primitives (via
/// <see cref="AllowableRange"/> and <see cref="TemperatureRange.IsExcursion"/>). The
/// lot-side behavior — appending a reading to a lot's history, flagging the lot at-risk,
/// raising the excursion event, and rejecting a zone-less lot — belongs to the
/// <c>RecordTemperatureReading</c> handler (task 24.3) operating on <c>GelLot</c>, not here.
/// </para>
/// </summary>
public sealed class TemperatureZone
{
    /// <summary>Smallest permitted zone capacity, in gel lots (Req 6.1).</summary>
    public const int MinCapacity = 1;

    /// <summary>Largest permitted zone capacity, in gel lots (Req 6.1).</summary>
    public const int MaxCapacity = 100_000;

    private TemperatureZone(ZoneId id, TemperatureRange allowableRange, int capacity, int storedQuantity)
    {
        Id = id;
        AllowableRange = allowableRange;
        Capacity = capacity;
        StoredQuantity = storedQuantity;
    }

    /// <summary>The zone's stable identity (Req 3.1).</summary>
    public ZoneId Id { get; }

    /// <summary>The inclusive allowable temperature band for lots stored in this zone (Req 6.1).</summary>
    public TemperatureRange AllowableRange { get; }

    /// <summary>Finite storage capacity in gel lots, guaranteed within 1..100000 (Req 6.1).</summary>
    public int Capacity { get; }

    /// <summary>Quantity currently stored in the zone. Mutated only through validated domain operations.</summary>
    public int StoredQuantity { get; private set; }

    /// <summary>Remaining available capacity = capacity − stored quantity (Req 7.5).</summary>
    public int RemainingCapacity => Capacity - StoredQuantity;

    /// <summary>
    /// Validated factory returning a <see cref="TemperatureZone"/> on success or a typed error on
    /// rejection (Req 6.1, 7.7). Rejects any <paramref name="capacity"/> outside the modeled
    /// 1..100000 range — including negatives (Req 7.7) and zero — with
    /// <see cref="DomainError.InvalidCapacity"/>, leaving no zone constructed. See the type-level
    /// remarks for the Req 6.1 / Req 7.7 reconciliation.
    /// </summary>
    /// <param name="id">The zone's identity.</param>
    /// <param name="allowableRange">The inclusive allowable temperature band.</param>
    /// <param name="capacity">Requested storage capacity; must be within 1..100000 (Req 6.1).</param>
    /// <param name="storedQuantity">Initial stored quantity; defaults to 0 and must be within [0, capacity].</param>
    public static Result<TemperatureZone> Create(
        ZoneId id,
        TemperatureRange allowableRange,
        int capacity,
        int storedQuantity = 0)
    {
        if (capacity < MinCapacity || capacity > MaxCapacity)
        {
            return DomainError.InvalidCapacity(
                $"Temperature zone capacity must be between {MinCapacity} and {MaxCapacity}; got {capacity}.");
        }

        if (storedQuantity < 0 || storedQuantity > capacity)
        {
            return DomainError.InvalidCapacity(
                $"Temperature zone initial stored quantity must be within [0, {capacity}]; got {storedQuantity}.");
        }

        return new TemperatureZone(id, allowableRange, capacity, storedQuantity);
    }

    /// <summary>
    /// Puts away <paramref name="quantity"/> gel lots into this zone, enforcing the zone capacity
    /// constraint (Req 7.1, 7.2, 7.6). Because <see cref="StoredQuantity"/> has a private setter,
    /// this mutating operation lives on the aggregate; the accept/reject decision is delegated to
    /// the shared <see cref="CapacityRule.CheckAdd(int, int, int)"/> so zones and starships behave
    /// identically (Property 4).
    /// <list type="bullet">
    ///   <item><description>
    ///     Permitted only when <c>StoredQuantity + quantity &lt;= Capacity</c> (Req 7.1); on success
    ///     <see cref="StoredQuantity"/> increases by <paramref name="quantity"/> and
    ///     <see cref="RemainingCapacity"/> decreases correspondingly.
    ///   </description></item>
    ///   <item><description>
    ///     A non-positive <paramref name="quantity"/> is rejected as an invalid quantity, leaving
    ///     <see cref="StoredQuantity"/> unchanged (Req 7.6).
    ///   </description></item>
    ///   <item><description>
    ///     A would-exceed put-away is rejected with <see cref="DomainError.CapacityExceeded(string, int, int)"/>
    ///     reporting the requested quantity and the remaining available capacity, leaving
    ///     <see cref="StoredQuantity"/> unchanged (Req 7.2).
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="quantity">The number of gel lots to store; must be strictly positive.</param>
    /// <returns>A successful <see cref="Result"/> when the put-away is applied, otherwise a typed rejection.</returns>
    public Result TryStore(int quantity)
    {
        var check = CapacityRule.CheckAdd(StoredQuantity, quantity, Capacity);
        if (check.IsFailure)
        {
            return check;
        }

        StoredQuantity += quantity;
        return Result.Success();
    }

    /// <summary>
    /// Removes <paramref name="quantity"/> gel lots from this zone for picking, the inverse of
    /// <see cref="TryStore(int)"/>. Delegates the accept/reject decision to the shared
    /// <see cref="CapacityRule.CheckRemove(int, int)"/>.
    /// <list type="bullet">
    ///   <item><description>
    ///     A non-positive <paramref name="quantity"/> is rejected as an invalid quantity, leaving
    ///     <see cref="StoredQuantity"/> unchanged (mirrors Req 7.6).
    ///   </description></item>
    ///   <item><description>
    ///     Removing more than is currently stored is rejected — reporting the requested quantity and
    ///     the amount actually available — leaving <see cref="StoredQuantity"/> unchanged.
    ///   </description></item>
    ///   <item><description>
    ///     On success <see cref="StoredQuantity"/> decreases by <paramref name="quantity"/> and
    ///     remains within <c>[0, Capacity]</c>.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="quantity">The number of gel lots to remove; must be strictly positive and no greater than <see cref="StoredQuantity"/>.</param>
    /// <returns>A successful <see cref="Result"/> when the removal is applied, otherwise a typed rejection.</returns>
    public Result TryRemove(int quantity)
    {
        var check = CapacityRule.CheckRemove(StoredQuantity, quantity);
        if (check.IsFailure)
        {
            return check;
        }

        StoredQuantity -= quantity;
        return Result.Success();
    }
}
