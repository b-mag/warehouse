using Forge.Domain.Common;

namespace Forge.Domain.Fulfillment;

/// <summary>
/// One lot chosen by <see cref="FefoSelector"/> together with the quantity drawn from it (Req 5.3).
/// <para>
/// A selection can partial-fill its last lot, so <see cref="Quantity"/> is the amount taken from the
/// lot for this request — never more than the lot's on-hand quantity, and (for every entry except
/// possibly the last) exactly the lot's full available quantity. The tuple of
/// (<see cref="LotId"/>, <see cref="Quantity"/>) is all a downstream picker needs to know which lots
/// to draw and how much from each; the actual on-hand decrement happens elsewhere (this selection is
/// a pure query — Req 5.5).
/// </para>
/// </summary>
/// <param name="LotId">The selected lot.</param>
/// <param name="Quantity">Units taken from that lot for this request (&gt; 0).</param>
public readonly record struct SelectedLot(GelLotId LotId, int Quantity);

/// <summary>
/// The outcome of a FEFO selection (Property 1 / Req 5). It reports exactly which lots were chosen and
/// how much from each (<see cref="SelectedLots"/>, in selection order), the <see cref="Fulfilled"/>
/// total, the <see cref="Shortfall"/>, and whether the fill was <see cref="IsPartial"/>.
/// <para>
/// <b>Invariants.</b> <see cref="SelectedLots"/> is ordered by the FEFO key
/// <c>(ExpiresAt, FefoPriority, GelLotId)</c> (Req 5.2). <see cref="Fulfilled"/> equals the sum of the
/// per-lot quantities and equals <c>min(requested, total selectable)</c> (Req 5.4).
/// <see cref="Shortfall"/> equals <c>requested − Fulfilled</c> and is always <c>&gt;= 0</c>.
/// <see cref="IsPartial"/> is <c>true</c> iff <see cref="Shortfall"/> is positive — including the
/// zero-selectable case, where <see cref="SelectedLots"/> is empty, <see cref="Fulfilled"/> is 0, and
/// <see cref="Shortfall"/> equals the full requested quantity (Req 5.4).
/// </para>
/// </summary>
/// <param name="SelectedLots">The chosen lots and per-lot quantities, in FEFO selection order (Req 5.2, 5.3).</param>
/// <param name="Fulfilled">Total units selected: <c>min(requested, total selectable)</c> (Req 5.4).</param>
/// <param name="Shortfall">Unmet units: <c>requested − Fulfilled</c>, always non-negative (Req 5.4).</param>
/// <param name="IsPartial">True iff <see cref="Shortfall"/> is positive (Req 5.4).</param>
public sealed record FulfillmentResult(
    IReadOnlyList<SelectedLot> SelectedLots,
    int Fulfilled,
    int Shortfall,
    bool IsPartial);
