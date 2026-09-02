using Forge.Domain.Common;
using Forge.Domain.Gels;

namespace Forge.Domain.Fulfillment;

/// <summary>
/// The First-Expired-First-Out fulfillment selector (Property 1 / Req 5). Given a requested quantity of
/// a gel type and the current inventory of lots, it chooses which lots to draw from, in FEFO order, until
/// the request is met or no selectable stock remains.
/// <para>
/// <b>Purity.</b> <see cref="Select"/> is a pure, stateless, deterministic query. It never mutates lots or
/// inventory and performs no I/O or randomness — the on-hand decrement is applied elsewhere by whoever acts
/// on the returned <see cref="FulfillmentResult"/>. Identical inventory state plus identical inputs always
/// yield the identical ordered selection and identical quantities (Req 5.6).
/// </para>
/// <para>
/// <b>Invalid request vs. partial fill.</b> Because the selector receives only a lot collection (not the gel
/// catalog), it cannot itself observe an "unknown gel type"; the caller owns that distinction. This selector
/// therefore treats the two Req 5 rejection-vs-partial cases as follows:
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Invalid request → rejected</b> (Req 5.5): a requested quantity below 1 (the valid range is
///       1..999,999,999) is rejected with <see cref="ErrorKind.InvalidRequest"/>, selecting nothing and
///       leaving inventory unchanged. Quantity is an <see cref="int"/>, so a "non-integer quantity" cannot
///       arise at this type-level boundary and needs no runtime check. A <c>null</c> lot sequence is likewise
///       an invalid request.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Zero selectable stock → partial</b> (Req 5.4): a valid request for which no matching, non-expired,
///       in-date lots exist yields a partial fill with an empty selection, <c>Fulfilled == 0</c>, and
///       <c>Shortfall == requested</c>. A caller that treats "no lots of this type" as an <em>unknown gel
///       type</em> (an invalid request per Req 5.5) must detect that against the catalog before calling and
///       reject it there; this selector reports it as a full-shortfall partial.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public static class FefoSelector
{
    /// <summary>The inclusive lower bound of a valid requested quantity (Req 5.1).</summary>
    public const int MinRequestedQuantity = 1;

    /// <summary>The inclusive upper bound of a valid requested quantity (Req 5.1).</summary>
    public const int MaxRequestedQuantity = 999_999_999;

    /// <summary>
    /// Select lots of <paramref name="gelType"/> to fulfill <paramref name="requestedQuantity"/> in FEFO order
    /// as of <paramref name="now"/>, without mutating anything (Req 5.1–5.6).
    /// <para>
    /// Selectable lots are those of the requested gel type that are not expired and whose
    /// <see cref="GelLot.ExpiresAt"/> is <b>strictly greater than</b> <paramref name="now"/> (Req 5.1). They are
    /// ordered ascending by the fully deterministic key <c>(ExpiresAt, FefoPriority, GelLotId)</c> (Req 5.2) and
    /// accumulated in that order — the last lot is partial-filled if it overshoots — until the cumulative
    /// quantity equals <paramref name="requestedQuantity"/> or no selectable lots remain (Req 5.3). If the total
    /// selectable quantity (possibly zero) is less than requested, the full selectable quantity is taken and a
    /// partial result with the corresponding shortfall is reported (Req 5.4).
    /// </para>
    /// </summary>
    /// <param name="gelType">The gel type to fulfill.</param>
    /// <param name="requestedQuantity">Requested units; must be in <c>1..999,999,999</c> (Req 5.1).</param>
    /// <param name="lots">The inventory to select from. Only lots matching <paramref name="gelType"/> are considered.</param>
    /// <param name="now">The current time used for the expiry cutoff (Req 5.1).</param>
    /// <returns>
    /// A successful <see cref="Result{T}"/> wrapping the <see cref="FulfillmentResult"/> for any valid request
    /// (full or partial, including zero-selectable), or a failure carrying
    /// <see cref="DomainError.InvalidRequest(string)"/> when the request is invalid (Req 5.5).
    /// </returns>
    public static Result<FulfillmentResult> Select(
        GelTypeId gelType,
        int requestedQuantity,
        IEnumerable<GelLot> lots,
        DateTimeOffset now)
    {
        // Req 5.5: reject an invalid request outright — select nothing, leave inventory untouched.
        if (lots is null)
        {
            return DomainError.InvalidRequest("Lot collection must not be null.");
        }

        if (requestedQuantity < MinRequestedQuantity)
        {
            return DomainError.InvalidRequest(
                $"Requested quantity must be at least {MinRequestedQuantity} (was {requestedQuantity}).");
        }

        if (requestedQuantity > MaxRequestedQuantity)
        {
            return DomainError.InvalidRequest(
                $"Requested quantity must be at most {MaxRequestedQuantity:N0} (was {requestedQuantity}).");
        }

        // Req 5.1: only this gel type's non-expired, in-date lots (expiry strictly after now) are selectable.
        // Req 5.2: order by the fully deterministic key (ExpiresAt, FefoPriority, GelLotId).
        var selectable = lots
            .Where(lot => lot.GelTypeId.Equals(gelType)
                && !lot.IsExpired
                && lot.ExpiresAt > now
                && lot.Quantity > 0)
            .OrderBy(lot => lot.ExpiresAt)
            .ThenBy(lot => lot.FefoPriority)
            .ThenBy(lot => lot.Id);

        // Req 5.3: accumulate in FEFO order, partial-filling the last lot, until met or exhausted.
        var selected = new List<SelectedLot>();
        var remaining = requestedQuantity;

        foreach (var lot in selectable)
        {
            if (remaining == 0)
            {
                break;
            }

            var take = Math.Min(lot.Quantity, remaining);
            selected.Add(new SelectedLot(lot.Id, take));
            remaining -= take;
        }

        // Req 5.4: fulfilled = min(requested, selectable); report shortfall + partial flag when short.
        var fulfilled = requestedQuantity - remaining;
        var shortfall = remaining;

        var result = new FulfillmentResult(
            SelectedLots: selected,
            Fulfilled: fulfilled,
            Shortfall: shortfall,
            IsPartial: shortfall > 0);

        return result;
    }
}
