using System.Collections.Concurrent;

using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Tasks;
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
    IReadOnlyList<Starship> Starships)
{
    /// <summary>
    /// The live, in-memory agent -> in-flight task link the task-execution stage maintains across ticks
    /// (task-execution stage). An agent executing a Pick / PutAway task has an entry here from the tick it
    /// is assigned the task until the tick it reaches the task's destination cell and the task completes;
    /// idle (patrolling) agents have no entry. This link is intentionally NOT a domain property of
    /// <see cref="Agent"/> (which stays a pure spatial entity) nor persisted yet — it lives with the same
    /// in-memory provider that owns the live agents, exactly as the tick-scoped spatial state does. When
    /// agent/task persistence lands, this moves behind the same seam with no change to the stage.
    /// <para>Mutable by design: the task-execution stage adds links on assignment and removes them on
    /// completion. The handler orders agents by id before iterating, so iteration is deterministic
    /// regardless of dictionary order.</para>
    /// </summary>
    public ConcurrentDictionary<AgentId, WarehouseTaskId> AgentTasks { get; init; } =
        new ConcurrentDictionary<AgentId, WarehouseTaskId>();

    /// <summary>
    /// Lots on the inbound train/conveyor that have been received but are not yet picked up
    /// by an assigned worker (rendered later).
    /// </summary>
    public IReadOnlyList<GelLotId> InboundQueueLotIds { get; set; } = Array.Empty<GelLotId>();

    /// <summary>
    /// Lots currently being carried (in-transit) by agents so the renderer can hide them from
    /// static zone rendering.
    /// </summary>
    public IReadOnlyList<GelLotId> InTransitLotIds { get; set; } = Array.Empty<GelLotId>();

    /// <summary>
    /// Link from a PutAway task id to the gel lot id it is meant to store, so we can attach
    /// carrying-cubes to agents executing that task.
    /// </summary>
    public ConcurrentDictionary<WarehouseTaskId, GelLotId> PutAwayTaskLotLinks { get; init; } =
        new ConcurrentDictionary<WarehouseTaskId, GelLotId>();

    /// <summary>
    /// Link from a Pick task id to the gel lot being carried outbound to a docked starship.
    /// </summary>
    public ConcurrentDictionary<WarehouseTaskId, GelLotId> PickTaskLotLinks { get; init; } =
        new ConcurrentDictionary<WarehouseTaskId, GelLotId>();

    /// <summary>
    /// Engine-owned starship lifecycle (Approaching / Docked / Loading / Departing / Away).
    /// Drives visuals and which ships may load; not derived from the long loading windows alone.
    /// </summary>
    public ConcurrentDictionary<StarshipId, StarshipRuntime> StarshipRuntimes { get; init; } =
        new ConcurrentDictionary<StarshipId, StarshipRuntime>();

    /// <summary>
    /// Single inbound train consist (Phase 1: one active visit at a time).
    /// </summary>
    public TrainRuntime Train { get; set; } = new();
}

/// <summary>
/// Per-starship runtime for the Phase-1 arrive / load / depart story.
/// One destination colony per ship in Phase 1 (multi-stop later).
/// </summary>
public sealed class StarshipRuntime
{
    public string Phase { get; set; } = StarshipPhases.Away;

    public DateTimeOffset PhaseEnteredAt { get; set; }

    /// <summary>Berth index while docked/loading; -1 when away / approaching / departing.</summary>
    public int DockIndex { get; set; } = -1;

    /// <summary>Inbound pallets still to unload on this visit (0 = arrive empty / ready to load).</summary>
    public int UnloadRemaining { get; set; }

    /// <summary>Lots discharged from this visit (tracked until put-away leaves inbound/transit).</summary>
    public List<GelLotId> InboundLotIds { get; set; } = new();

    /// <summary>True after inbound receipts were issued for the current produce-bearing visit.</summary>
    public bool InboundCargoMaterialized { get; set; }

    /// <summary>Driver sets this when Approaching needs inbound produce created this tick.</summary>
    public bool NeedsInboundMaterialization { get; set; }

    /// <summary>How many inbound lots to create when materializing (set at visit start).</summary>
    public int PendingInboundLotCount { get; set; }

    /// <summary>Record materialized inbound lots; keeps phase Approaching until approach duration ends.</summary>
    public void MarkInboundMaterialized(IReadOnlyList<GelLotId> lotIds)
    {
        ArgumentNullException.ThrowIfNull(lotIds);
        InboundLotIds = lotIds.ToList();
        UnloadRemaining = lotIds.Count;
        InboundCargoMaterialized = true;
        NeedsInboundMaterialization = false;
        PendingInboundLotCount = 0;
    }
}

/// <summary>Authoritative inbound train phases projected to clients.</summary>
public static class TrainPhases
{
    public const string Approaching = "Approaching";
    public const string Unloading = "Unloading";
    public const string Departing = "Departing";
    public const string Away = "Away";
}

/// <summary>Single inbound train consist runtime.</summary>
public sealed class TrainRuntime
{
    public Guid Id { get; set; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    public string Phase { get; set; } = TrainPhases.Away;

    public DateTimeOffset PhaseEnteredAt { get; set; }

    public int CarCount { get; set; }

    /// <summary>Lots that arrived with this consist (still on rail until picked up).</summary>
    public List<GelLotId> LotIds { get; set; } = new();

    /// <summary>Sim day index for which the next visit was already scheduled (legacy; prefer NextArrivalAt).</summary>
    public int LastScheduledDayIndex { get; set; } = -1;

    /// <summary>Which train slot within the day was last started (0-based).</summary>
    public int LastSlotIndex { get; set; } = -1;

    /// <summary>Next scheduled arrival (sim clock). Null means "arrive on the next Away evaluation".</summary>
    public DateTimeOffset? NextArrivalAt { get; set; }

    /// <summary>True after cargo receipts were issued for the current visit.</summary>
    public bool CargoMaterialized { get; set; }

    /// <summary>Driver sets this when Approaching needs cargo created this tick.</summary>
    public bool NeedsCargoMaterialization { get; set; }

    /// <summary>
    /// Called by the Simulation driver after issuing inbound receipts for this visit.
    /// Keeps phase Approaching so the consist can roll in already loaded.
    /// </summary>
    public void MarkCargoMaterialized(IReadOnlyList<GelLotId> lotIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(lotIds);

        LotIds = lotIds.ToList();
        int cars = Math.Max(
            1,
            (int)Math.Ceiling(lotIds.Count / (double)VisualSimulationConstants.TrainCarCapacityPallets));
        CarCount = Math.Min(VisualSimulationConstants.MaxTrainCars, cars);
        CargoMaterialized = true;
        NeedsCargoMaterialization = false;
        _ = now;
    }
}

/// <summary>Authoritative starship phase strings projected to clients.</summary>
public static class StarshipPhases
{
    public const string Approaching = "Approaching";
    public const string Docked = "Docked";
    public const string Unloading = "Unloading";
    public const string Loading = "Loading";
    public const string Departing = "Departing";
    public const string Away = "Away";
}

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

    /// <summary>
    /// Notify the tick-scoped in-memory spatial store that a new inbound lot has been received and a
    /// PutAway task was created for it. Used to seed the inbound train / carrying visuals.
    /// </summary>
    void EnqueueInboundPutAway(GelLotId lotId, WarehouseTaskId putAwayTaskId) { }

    /// <summary>
    /// Apply the current operator "workers on shift" control by adjusting the active movement
    /// agents used by the tick stages and renderer. Implementations must preserve any in-flight
    /// agent/task links so active tasks can still complete.
    /// </summary>
    void ApplyWorkerCount(int workersOnShift) { }
}
