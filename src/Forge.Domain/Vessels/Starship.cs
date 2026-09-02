using Forge.Domain.Capacity;
using Forge.Domain.Common;

namespace Forge.Domain.Vessels;

/// <summary>
/// A vessel that transports gel lots to a <see cref="Destination"/> colony, constrained by a finite
/// cargo capacity and one or more scheduled <see cref="LoadingWindow"/>s (Req 13.1). The starship
/// tracks how much is currently loaded and exposes its remaining available capacity (Req 7.5).
/// <para>
/// Construction goes through the validated <see cref="Create"/> factory so an invalid starship can
/// never exist: cargo capacity must be <c>&gt;= 0</c> (Req 13.1, 7.7) and at least one loading window
/// must be supplied (Req 13.1). Loading enforces the shared capacity rule so starships and
/// <see cref="Forge.Domain.ColdChain.TemperatureZone"/>s accept and reject under identical,
/// deterministic terms (Property 4); <see cref="TryLoad(int)"/> delegates to
/// <see cref="CapacityRule.CheckAdd(int, int, int)"/> exactly as the zone side does.
/// </para>
/// <para>
/// This type provides the domain model plus the pure window-admission and capacity helpers only.
/// The reject-load-when-outside-window flow (Req 13.3) and the window-close shortfall
/// <c>LoadingWindowClosed</c> event (Req 13.6) are Application concerns (task 21.1) that consume the
/// <see cref="IsWithinAnyWindow(DateTimeOffset)"/> / <see cref="NextWindow(DateTimeOffset)"/>
/// admission helpers and <see cref="TryLoad(int)"/>; they are not implemented here.
/// </para>
/// </summary>
public sealed class Starship
{
    private readonly List<LoadingWindow> _windows;

    private Starship(
        StarshipId id,
        int cargoCapacity,
        ColonyId destination,
        List<LoadingWindow> windows,
        int loadedQuantity)
    {
        Id = id;
        CargoCapacity = cargoCapacity;
        Destination = destination;
        _windows = windows;
        LoadedQuantity = loadedQuantity;
    }

    /// <summary>The starship's stable identity (Req 3.1).</summary>
    public StarshipId Id { get; }

    /// <summary>Finite cargo capacity in gel lots, guaranteed <c>&gt;= 0</c> (Req 13.1, 7.7).</summary>
    public int CargoCapacity { get; }

    /// <summary>The colony this starship delivers to (Req 13.1).</summary>
    public ColonyId Destination { get; }

    /// <summary>The scheduled loading windows, guaranteed non-empty (Req 13.1).</summary>
    public IReadOnlyList<LoadingWindow> Windows => _windows;

    /// <summary>Quantity currently loaded. Mutated only through validated domain operations.</summary>
    public int LoadedQuantity { get; private set; }

    /// <summary>Remaining available cargo capacity = capacity − loaded quantity (Req 7.5).</summary>
    public int RemainingCapacity => CargoCapacity - LoadedQuantity;

    /// <summary>
    /// Validated factory returning a <see cref="Starship"/> on success or a typed error on rejection
    /// (Req 13.1, 7.7). Rejects a negative <paramref name="cargoCapacity"/> with
    /// <see cref="DomainError.InvalidCapacity(string)"/> (Req 7.7), an empty or null
    /// <paramref name="windows"/> set with <see cref="DomainError.InvalidValue(string)"/> (Req 13.1
    /// requires one or more windows), and a <paramref name="loadedQuantity"/> outside
    /// <c>[0, cargoCapacity]</c> with <see cref="DomainError.InvalidCapacity(string)"/>. On success no
    /// invalid starship is ever constructed.
    /// </summary>
    /// <param name="id">The starship's identity.</param>
    /// <param name="cargoCapacity">Cargo capacity in gel lots; must be <c>&gt;= 0</c> (Req 13.1, 7.7).</param>
    /// <param name="destination">The destination colony.</param>
    /// <param name="windows">One or more scheduled loading windows (Req 13.1).</param>
    /// <param name="loadedQuantity">Initial loaded quantity; defaults to 0 and must be within [0, cargoCapacity].</param>
    public static Result<Starship> Create(
        StarshipId id,
        int cargoCapacity,
        ColonyId destination,
        IEnumerable<LoadingWindow> windows,
        int loadedQuantity = 0)
    {
        if (cargoCapacity < 0)
        {
            return DomainError.InvalidCapacity(
                $"Starship cargo capacity must be greater than or equal to zero; got {cargoCapacity}.");
        }

        var windowList = windows is null ? new List<LoadingWindow>() : new List<LoadingWindow>(windows);
        if (windowList.Count == 0)
        {
            return DomainError.InvalidValue(
                "Starship must have one or more scheduled loading windows; got none.");
        }

        if (loadedQuantity < 0 || loadedQuantity > cargoCapacity)
        {
            return DomainError.InvalidCapacity(
                $"Starship initial loaded quantity must be within [0, {cargoCapacity}]; got {loadedQuantity}.");
        }

        return new Starship(id, cargoCapacity, destination, windowList, loadedQuantity);
    }

    /// <summary>
    /// Loads <paramref name="quantity"/> gel lots onto this starship, enforcing the cargo capacity
    /// constraint (Req 7.3, 7.4, 7.6). This is the wiring described by task 8.1's
    /// <see cref="CapacityRule"/>: the accept/reject decision is delegated to
    /// <see cref="CapacityRule.CheckAdd(int, int, int)"/> with
    /// <c>(LoadedQuantity, quantity, CargoCapacity)</c> so starships and zones behave identically
    /// (Property 4).
    /// <list type="bullet">
    ///   <item><description>
    ///     Permitted only when <c>LoadedQuantity + quantity &lt;= CargoCapacity</c> (Req 7.3); on
    ///     success <see cref="LoadedQuantity"/> increases by <paramref name="quantity"/> and
    ///     <see cref="RemainingCapacity"/> decreases correspondingly.
    ///   </description></item>
    ///   <item><description>
    ///     A non-positive <paramref name="quantity"/> is rejected as an invalid quantity, leaving
    ///     <see cref="LoadedQuantity"/> unchanged (Req 7.6).
    ///   </description></item>
    ///   <item><description>
    ///     A would-exceed load is rejected with <see cref="DomainError.CapacityExceeded(string, int, int)"/>
    ///     reporting the requested quantity and the remaining available capacity, leaving
    ///     <see cref="LoadedQuantity"/> unchanged (Req 7.4).
    ///   </description></item>
    /// </list>
    /// The loading-window admission check (Req 13.2, 13.3) is layered on top by the Application rule
    /// (task 21.1) using <see cref="IsWithinAnyWindow(DateTimeOffset)"/>; it is intentionally not
    /// enforced here so this pure capacity operation stays clock-free and deterministic.
    /// </summary>
    /// <param name="quantity">The number of gel lots to load; must be strictly positive.</param>
    /// <returns>A successful <see cref="Result"/> when the load is applied, otherwise a typed rejection.</returns>
    public Result TryLoad(int quantity)
    {
        var check = CapacityRule.CheckAdd(LoadedQuantity, quantity, CargoCapacity);
        if (check.IsFailure)
        {
            return check;
        }

        LoadedQuantity += quantity;
        return Result.Success();
    }

    /// <summary>
    /// Pure admission helper: returns <c>true</c> when <paramref name="now"/> is inside any of the
    /// starship's loading windows using inclusive bounds (Req 13.2). Deterministic and side-effect
    /// free; consumed by the Application loading rule (task 21.1) to decide whether a load is
    /// permitted at the current simulated time.
    /// </summary>
    /// <param name="now">The current simulated time to test.</param>
    /// <returns><c>true</c> if <paramref name="now"/> falls within at least one window.</returns>
    public bool IsWithinAnyWindow(DateTimeOffset now)
    {
        foreach (var window in _windows)
        {
            if (window.IsOpenAt(now))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pure helper returning the earliest loading window whose <see cref="LoadingWindow.Start"/> is
    /// strictly after <paramref name="now"/>, or <c>null</c> when no such window exists (Req 13.3).
    /// The Application reject-out-of-window flow (task 21.1) uses this to report the start time of the
    /// next loading window when a load is attempted outside every window. Deterministic and
    /// side-effect free.
    /// </summary>
    /// <param name="now">The current simulated time.</param>
    /// <returns>The earliest future window by start time, or <c>null</c> if none starts after <paramref name="now"/>.</returns>
    public LoadingWindow? NextWindow(DateTimeOffset now)
    {
        LoadingWindow? next = null;
        foreach (var window in _windows)
        {
            if (window.Start > now && (next is null || window.Start < next.Start))
            {
                next = window;
            }
        }

        return next;
    }
}
