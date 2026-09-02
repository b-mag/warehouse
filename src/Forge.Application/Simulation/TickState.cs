using Forge.Domain.Spatial;
using Forge.Domain.Vessels;

namespace Forge.Application.Simulation;

/// <summary>
/// The in-memory, tick-scoped spatial and vessel state the per-tick rule application
/// (<see cref="ApplyTickRulesHandler"/>, task 24.4) operates over during a single tick.
/// <para>
/// <b>Why this exists.</b> The movement (stage 3) and starship-loading (stage 4) rule stages need the
/// live <see cref="Agent"/>s, the <see cref="WarehouseGrid"/> they move on, the segment
/// <see cref="ReservationLedger"/>, and the <see cref="Starship"/>s being loaded. Unlike gel lots,
/// orders, tasks, and workers, these do not yet have a repository seam
/// (<c>Forge.Application.Abstractions.Repositories</c> defines <c>IGelLotRepository</c>,
/// <c>IOrderRepository</c>, <c>ITaskRepository</c>, <c>IWorkerRepository</c>, <c>IZoneRepository</c>,
/// <c>IColonyRepository</c> — but no agent/starship/grid repository). The tick pipeline therefore reads
/// this spatial/vessel state through the <see cref="ITickStateProvider"/> abstraction, exactly as the
/// task guidance permits: "If a collaborator type you need does not exist yet, implement the stage
/// against the abstraction and note it." When agent/starship/grid persistence lands (a later
/// persistence task), this provider is the single seam an EF-backed implementation slots behind — no
/// change to the handler.
/// </para>
/// <para>
/// <b>Determinism.</b> The provider exposes agents and starships as read-only lists; the handler sorts
/// them by id before iterating, so iteration order never depends on how the provider stores them
/// (Req 19.6 reproducibility). The provider holds no clock and no RNG.
/// </para>
/// </summary>
/// <param name="Grid">The warehouse grid agents move over (Req 18.1).</param>
/// <param name="Agents">
/// The live movement agents. The handler orders these by <see cref="Agent.Id"/> before advancing them,
/// so the supplied order is immaterial to the result (Req 18.4, 19.6).
/// </param>
/// <param name="Ledger">
/// The single segment-reservation grant point shared across agents this tick (Req 19.1, 19.3).
/// </param>
/// <param name="Starships">
/// The starships eligible for loading this tick, ordered by the handler before the loading stage runs
/// (Req 13.2).
/// </param>
public sealed record TickState(
    WarehouseGrid Grid,
    IReadOnlyList<Agent> Agents,
    ReservationLedger Ledger,
    IReadOnlyList<Starship> Starships);

/// <summary>
/// The seam through which <see cref="ApplyTickRulesHandler"/> reads the tick-scoped spatial/vessel
/// state (see <see cref="TickState"/> for why this is a distinct abstraction rather than a repository).
/// An implementation lives with the wired driver / a future persistence layer; the WMS Core Application
/// depends only on this abstraction, preserving the layer boundary (Application → Domain + Contracts).
/// </summary>
public interface ITickStateProvider
{
    /// <summary>
    /// Return the current spatial/vessel state for the tick about to be applied, or <see langword="null"/>
    /// when no spatial subsystem is wired (a headless/cold-chain-only deployment). A <see langword="null"/>
    /// result makes the movement and starship-loading stages deterministic no-ops for the tick, leaving
    /// the expiry-decay, order-intake, and metrics stages fully active.
    /// </summary>
    Task<TickState?> GetTickStateAsync(CancellationToken ct = default);
}
