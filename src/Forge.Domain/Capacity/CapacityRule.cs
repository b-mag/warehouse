using Forge.Domain.Common;

namespace Forge.Domain.Capacity;

/// <summary>
/// The single, shared, pure capacity rule enforced by every quantity-holding aggregate that has a
/// finite capacity — a <see cref="Forge.Domain.ColdChain.TemperatureZone"/> for put-away and a
/// <c>Starship</c> for cargo loading (Req 7). Centralizing the arithmetic here guarantees zones and
/// starships reject and accept under identical, deterministic terms and lets Property 4 — the
/// capacity invariant across both aggregate kinds — be reasoned about in one place.
/// <para>
/// The rule is a pure, deterministic function of its integer inputs (no clock, no I/O, no state):
/// identical inputs always yield an identical outcome, and it never mutates anything. Callers apply
/// the returned delta to their own state only on success, which is what lets a rejection leave the
/// aggregate's stored/loaded quantity unchanged (Req 7.2, 7.4, 7.6).
/// </para>
/// <para>
/// <b>Coordination with task 9.1 (Starship).</b> The zone side (<c>TemperatureZone.TryStore</c> /
/// <c>TryRemove</c>) is implemented in task 8.1 on the aggregate itself, delegating to this helper.
/// The <c>Starship</c> aggregate is authored by parallel task 9.1 in <c>Forge.Domain.Vessels</c>;
/// its <c>TryLoad(int)</c> should call <see cref="CheckAdd(int, int, int)"/> with
/// <c>(LoadedQuantity, quantity, CargoCapacity)</c> and, on success, increase
/// <c>LoadedQuantity</c> by <paramref name="quantity"/> — mirroring the zone side exactly. This
/// helper is intentionally aggregate-agnostic so task 9.1 needs no change here.
/// </para>
/// </summary>
public static class CapacityRule
{
    /// <summary>
    /// Validates adding <paramref name="requested"/> units to a location currently holding
    /// <paramref name="current"/> units against a finite <paramref name="capacity"/>, and reports
    /// the outcome without mutating anything (Req 7.1–7.4, 7.6, 7.7). Rejection cases, in order:
    /// <list type="number">
    ///   <item><description>
    ///     <b>Invalid capacity</b> — a negative <paramref name="capacity"/> is not a valid
    ///     configuration and is rejected with <see cref="ErrorKind.InvalidCapacity"/> (Req 7.7).
    ///     The aggregate factories already guard this, but the shared rule treats capacity as
    ///     <c>&gt;= 0</c> too so the invariant holds even if called directly.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Non-positive quantity</b> — a <paramref name="requested"/> quantity that is not
    ///     strictly greater than zero is rejected with <see cref="ErrorKind.CapacityExceeded"/>
    ///     as an "invalid quantity" (Req 7.6), reporting the requested value and the current
    ///     remaining capacity.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Would exceed</b> — when <c>current + requested &gt; capacity</c> the add is rejected
    ///     with <see cref="ErrorKind.CapacityExceeded"/> reporting both the requested quantity and
    ///     the remaining available capacity (Req 7.2, 7.4).
    ///   </description></item>
    /// </list>
    /// On success the operation is permitted only when <c>current + requested &lt;= capacity</c>
    /// (Req 7.1, 7.3); the caller then increases its own quantity by <paramref name="requested"/>.
    /// </summary>
    /// <param name="current">The quantity currently stored/loaded. Assumed non-negative and within capacity by the aggregate's own invariant.</param>
    /// <param name="requested">The quantity to add; must be strictly positive.</param>
    /// <param name="capacity">The location's finite capacity; must be <c>&gt;= 0</c>.</param>
    /// <returns>A successful <see cref="Result"/> when the add is permitted, otherwise a typed rejection.</returns>
    public static Result CheckAdd(int current, int requested, int capacity)
    {
        if (capacity < 0)
        {
            return DomainError.InvalidCapacity(
                $"Capacity must be greater than or equal to zero; got {capacity}.");
        }

        var remaining = capacity - current;

        if (requested <= 0)
        {
            return DomainError.CapacityExceeded(
                $"Requested quantity must be a positive value greater than zero; got {requested}.",
                requested,
                remaining);
        }

        if (requested > remaining)
        {
            return DomainError.CapacityExceeded(
                $"Requested quantity {requested} exceeds remaining capacity {remaining}.",
                requested,
                remaining);
        }

        return Result.Success();
    }

    /// <summary>
    /// Validates removing <paramref name="requested"/> units from a location currently holding
    /// <paramref name="current"/> units, without mutating anything. Used by picking/unloading
    /// (the inverse of <see cref="CheckAdd(int, int, int)"/>). Rejection cases:
    /// <list type="number">
    ///   <item><description>
    ///     <b>Non-positive quantity</b> — a <paramref name="requested"/> quantity that is not
    ///     strictly greater than zero is rejected with <see cref="ErrorKind.CapacityExceeded"/>
    ///     (an "invalid quantity"), consistent with the put-away/load rule (Req 7.6).
    ///   </description></item>
    ///   <item><description>
    ///     <b>More than stored</b> — removing more than is currently held would drive the quantity
    ///     negative, so it is rejected with <see cref="ErrorKind.CapacityExceeded"/> reporting the
    ///     requested quantity and the amount actually available (<paramref name="current"/>).
    ///   </description></item>
    /// </list>
    /// On success the caller decreases its own quantity by <paramref name="requested"/>, leaving it
    /// in <c>[0, capacity]</c>. Rejection leaves the caller's state unchanged.
    /// </summary>
    /// <param name="current">The quantity currently stored/loaded; assumed non-negative.</param>
    /// <param name="requested">The quantity to remove; must be strictly positive and no greater than <paramref name="current"/>.</param>
    /// <returns>A successful <see cref="Result"/> when the removal is permitted, otherwise a typed rejection.</returns>
    public static Result CheckRemove(int current, int requested)
    {
        if (requested <= 0)
        {
            return DomainError.CapacityExceeded(
                $"Requested quantity must be a positive value greater than zero; got {requested}.",
                requested,
                current);
        }

        if (requested > current)
        {
            return DomainError.CapacityExceeded(
                $"Requested quantity {requested} exceeds the currently stored quantity {current}.",
                requested,
                current);
        }

        return Result.Success();
    }
}
