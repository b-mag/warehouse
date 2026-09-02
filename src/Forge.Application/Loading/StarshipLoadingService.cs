using Forge.Domain.Common;
using Forge.Domain.Events;
using Forge.Domain.Fulfillment;
using Forge.Domain.Gels;
using Forge.Domain.Vessels;

namespace Forge.Application.Loading;

/// <summary>
/// The Application-layer starship-loading rule (task 21.1, Req 13.2–13.4, 13.6). It layers the
/// loading-window admission check, FEFO load selection, and the window-close shortfall event on top
/// of the pure <see cref="Starship"/> domain helpers, which stay clock-free and deterministic.
/// <para>
/// This service is the piece the per-tick pipeline (task 24.4) calls during its "Starship loading"
/// stage: it decides whether a load is permitted at the current simulated time, selects lots in FEFO
/// order for the load, applies the capacity-checked load to the starship, and — when a window closes —
/// produces the <see cref="LoadingWindowClosed"/> domain event reporting loaded quantity and shortfall.
/// </para>
/// <para>
/// <b>Purity.</b> The service holds no state and performs no I/O, randomness, or clock access. The
/// caller supplies <c>now</c> and the current inventory, so every method is a deterministic function of
/// its inputs (identical starship state + inputs yield identical outcomes). State mutation is confined
/// to the validated <see cref="Starship.TryLoad(int)"/> call, which the domain guards with the shared
/// capacity rule (Req 13.5); a rejected load leaves the starship's loaded quantity unchanged.
/// </para>
/// </summary>
public sealed class StarshipLoadingService
{
    /// <summary>
    /// Attempt to load <paramref name="requestedQuantity"/> units of <paramref name="gelType"/> onto
    /// <paramref name="starship"/> at simulated time <paramref name="now"/>, selecting lots in FEFO
    /// order (Req 13.2, 13.3, 13.4, 13.5).
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Window admission (Req 13.2, 13.3).</b> A load is permitted only while <paramref name="now"/>
    ///     falls within <c>[window.Start, window.End]</c> of some loading window
    ///     (<see cref="Starship.IsWithinAnyWindow(DateTimeOffset)"/>). A load requested outside every
    ///     window is rejected with <see cref="DomainError.WindowClosed(string)"/>, leaving the loaded
    ///     quantity unchanged; when a later window exists, its start time is reported in the error
    ///     detail under <c>"nextWindowStart"</c> (and the window itself under <c>"nextWindow"</c>) via
    ///     <see cref="Starship.NextWindow(DateTimeOffset)"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>FEFO selection (Req 13.4).</b> When admitted, lots are chosen by
    ///     <see cref="FefoSelector.Select(GelTypeId, int, System.Collections.Generic.IEnumerable{GelLot}, DateTimeOffset)"/>
    ///     in ascending <c>(ExpiresAt, FefoPriority, GelLotId)</c> order. An invalid request (quantity
    ///     below 1) is rejected by the selector, leaving the loaded quantity unchanged.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Capacity (Req 13.5).</b> The picked quantity is applied through
    ///     <see cref="Starship.TryLoad(int)"/>, which rejects a would-exceed load reporting remaining
    ///     capacity and leaves the loaded quantity unchanged. FEFO may fulfill fewer units than
    ///     requested (a partial fill); only the fulfilled quantity is loaded.
    ///   </description></item>
    /// </list>
    /// </summary>
    /// <param name="starship">The starship to load. Its loaded quantity is mutated only on success.</param>
    /// <param name="gelType">The gel type to draw lots of.</param>
    /// <param name="requestedQuantity">Requested units; validated by the FEFO selector.</param>
    /// <param name="lots">The inventory to select from.</param>
    /// <param name="now">The current simulated time used for window admission and the FEFO expiry cutoff.</param>
    /// <returns>
    /// A successful <see cref="Result{LoadOutcome}"/> describing which lots were loaded and how much on
    /// admission + a valid request + a within-capacity load; otherwise a typed rejection
    /// (<see cref="ErrorKind.WindowClosed"/>, <see cref="ErrorKind.InvalidRequest"/>, or
    /// <see cref="ErrorKind.CapacityExceeded"/>) that leaves the starship's loaded quantity unchanged.
    /// </returns>
    public Result<LoadOutcome> TryLoad(
        Starship starship,
        GelTypeId gelType,
        int requestedQuantity,
        IEnumerable<GelLot> lots,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(starship);

        // Req 13.2 / 13.3: admit only within a window; otherwise reject and report the next window.
        if (!starship.IsWithinAnyWindow(now))
        {
            var next = starship.NextWindow(now);
            var message = next is null
                ? $"Load rejected: {now:o} is outside every loading window and no future window exists."
                : $"Load rejected: {now:o} is outside every loading window; next window starts {next.Start:o}.";

            var error = DomainError.WindowClosed(message);
            if (next is not null)
            {
                error = error
                    .WithDetail("nextWindowStart", next.Start)
                    .WithDetail("nextWindow", next);
            }

            return error;
        }

        // Req 13.4: select lots in FEFO order. An invalid request is rejected here, leaving state unchanged.
        var selection = FefoSelector.Select(gelType, requestedQuantity, lots, now);
        if (selection.IsFailure)
        {
            return selection.Error;
        }

        var fulfillment = selection.Value;

        // Nothing selectable: a valid but fully-shorted request loads nothing and leaves state unchanged.
        if (fulfillment.Fulfilled == 0)
        {
            return new LoadOutcome(
                RequestedQuantity: requestedQuantity,
                LoadedQuantity: 0,
                Shortfall: requestedQuantity,
                SelectedLots: fulfillment.SelectedLots);
        }

        // Req 13.5: apply the FEFO-picked quantity through the capacity-guarded domain load. A
        // would-exceed load is rejected here, reporting remaining capacity and leaving state unchanged.
        var loaded = starship.TryLoad(fulfillment.Fulfilled);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        return new LoadOutcome(
            RequestedQuantity: requestedQuantity,
            LoadedQuantity: fulfillment.Fulfilled,
            Shortfall: requestedQuantity - fulfillment.Fulfilled,
            SelectedLots: fulfillment.SelectedLots);
    }

    /// <summary>
    /// Produce the <see cref="LoadingWindowClosed"/> domain event for a starship whose loading window
    /// has closed (Req 13.6). This is the method the per-tick pipeline (task 24.4) calls when it
    /// detects a window close during the "Starship loading" stage.
    /// <para>
    /// The event reports the quantity actually loaded and a shortfall equal to
    /// <c>requested − loaded</c>, reporting zero when the request was fully loaded. A negative computed
    /// shortfall (loaded exceeding requested, which the loading rule never produces) is clamped to zero
    /// so the reported shortfall is always non-negative.
    /// </para>
    /// </summary>
    /// <param name="starshipId">The starship whose window closed.</param>
    /// <param name="requestedQuantity">The total quantity that was requested for the window.</param>
    /// <param name="loadedQuantity">The quantity actually loaded during the window.</param>
    /// <param name="closedAt">The simulated time at which the window closed (the event timestamp).</param>
    /// <returns>The <see cref="LoadingWindowClosed"/> event to publish.</returns>
    public LoadingWindowClosed CloseWindow(
        StarshipId starshipId,
        int requestedQuantity,
        int loadedQuantity,
        DateTimeOffset closedAt)
    {
        // Req 13.6: shortfall = requested - loaded, zero when fully loaded (and never negative).
        var shortfall = Math.Max(0, requestedQuantity - loadedQuantity);
        return new LoadingWindowClosed(starshipId, loadedQuantity, shortfall, closedAt);
    }
}

/// <summary>
/// The result of a successful <see cref="StarshipLoadingService.TryLoad"/>: the requested quantity, the
/// quantity actually loaded (the FEFO-fulfilled amount, capped by inventory), the resulting
/// <see cref="Shortfall"/> (<c>requested − loaded</c>, always non-negative), and the FEFO-ordered lots
/// drawn from with the per-lot quantities taken. The window-close shortfall event is produced
/// separately by <see cref="StarshipLoadingService.CloseWindow"/> (Req 13.6).
/// </summary>
/// <param name="RequestedQuantity">The units requested for this load.</param>
/// <param name="LoadedQuantity">The units actually loaded onto the starship (Req 13.4, 13.5).</param>
/// <param name="Shortfall">Unmet units for this load: <c>requested − loaded</c>, always non-negative.</param>
/// <param name="SelectedLots">The FEFO-ordered lots and per-lot quantities drawn for the load (Req 13.4).</param>
public sealed record LoadOutcome(
    int RequestedQuantity,
    int LoadedQuantity,
    int Shortfall,
    IReadOnlyList<SelectedLot> SelectedLots);
