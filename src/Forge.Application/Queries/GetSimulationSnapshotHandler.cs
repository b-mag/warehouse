using Forge.Application.Abstractions.Repositories;
using Forge.Application.Docks;
using Forge.Application.Abstractions;
using Forge.Application.OperatorParameters;
using Forge.Application.Simulation;
using Forge.Contracts.Dtos;
using Forge.Domain.ColdChain;
using Forge.Domain.Common;
using Forge.Domain.Gels;
using Forge.Domain.Spatial;
using Forge.Domain.Vessels;

namespace Forge.Application.Queries;

/// <summary>
/// The read-only simulation-snapshot query handler (task 24.8, Req 9.3, 23.3). It projects the current
/// inventory, order/task-derived, starship, agent, metrics, and operator-parameter state into an
/// immutable <see cref="SimulationSnapshotDto"/> for a connecting client's initial snapshot (Req 23.3)
/// or an ad-hoc state query (Req 9.3), invoked through
/// <see cref="Forge.Application.Abstractions.IWarehouseCommandGateway.GetSnapshotAsync"/>.
/// <para>
/// <b>Strictly read-only (Req 9.3).</b> This handler only <em>reads</em>: it calls the query methods of
/// the repository abstractions (<see cref="IZoneRepository.ListAllAsync"/>,
/// <see cref="IGelLotRepository.ListAllAsync"/>) and the read-only projections of the in-memory
/// Application components (<see cref="WarehouseMetrics.ToDto"/>, <see cref="OperatorParameterState.ToDto"/>,
/// <see cref="DockScheduler.Backlog"/>/<see cref="DockScheduler.UtilizationOf"/>, and the tick-state
/// projection through <see cref="ITickStateProvider"/>). It never stages a repository <c>Add</c>/<c>Update</c>,
/// never commits an <see cref="IUnitOfWork"/>, never publishes an event, and never mutates any domain
/// aggregate — so building a snapshot leaves simulation state byte-for-byte unchanged.
/// </para>
/// <para>
/// <b>Reuses existing read abstractions.</b> Zones and lots come from their existing repositories;
/// agents and starships come from the existing <see cref="ITickStateProvider"/> seam (the same seam the
/// per-tick pipeline reads spatial/vessel state through — there is no agent/starship repository yet).
/// A <see langword="null"/> tick state (a headless/cold-chain-only deployment with no spatial subsystem
/// wired) projects to empty agent and starship lists rather than failing, mirroring how the tick pipeline
/// treats a null tick state as a no-op for those stages.
/// </para>
/// <para>
/// This type lives in <c>Forge.Application</c> and depends only on the Application abstractions/components
/// plus the pure Domain and Contracts DTOs — never on concrete Infrastructure types or the Simulation
/// project (Req 9.5), preserving the layer boundary the architecture tests enforce.
/// </para>
/// </summary>
public sealed class GetSimulationSnapshotHandler
{
    private readonly IZoneRepository _zones;
    private readonly IGelLotRepository _lots;
    private readonly ITickStateProvider _tickState;
    private readonly IClock _clock;
    private readonly WarehouseMetrics _metrics;
    private readonly DockScheduler _dockScheduler;
    private readonly DockBayId _primaryDockBay;
    private readonly OperatorParameterState _parameters;

    /// <summary>
    /// Construct the query handler from the read abstractions and read-only Application components it
    /// projects (Req 9.5). None of these are mutated by <see cref="HandleAsync"/>.
    /// </summary>
    /// <param name="zones">Source of the current temperature zones (read-only <c>ListAllAsync</c>).</param>
    /// <param name="lots">Source of the current gel lots / inventory (read-only <c>ListAllAsync</c>).</param>
    /// <param name="tickState">
    /// The spatial/vessel read seam supplying the live agents and starships; a <see langword="null"/>
    /// tick state yields empty agent/starship projections.
    /// </param>
    /// <param name="metrics">The backlog/throughput metrics component, projected via its read-only <c>ToDto</c>.</param>
    /// <param name="dockScheduler">
    /// The dock scheduler supplying dock contention + utilization for the metrics projection (read-only).
    /// </param>
    /// <param name="primaryDockBay">The dock bay whose utilization the snapshot reports (Req 17.4).</param>
    /// <param name="parameters">The live operator-parameter state, projected via its read-only <c>ToDto</c>.</param>
    public GetSimulationSnapshotHandler(
        IZoneRepository zones,
        IGelLotRepository lots,
        ITickStateProvider tickState,
        IClock clock,
        WarehouseMetrics metrics,
        DockScheduler dockScheduler,
        DockBayId primaryDockBay,
        OperatorParameterState parameters)
    {
        _zones = zones ?? throw new ArgumentNullException(nameof(zones));
        _lots = lots ?? throw new ArgumentNullException(nameof(lots));
        _tickState = tickState ?? throw new ArgumentNullException(nameof(tickState));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _dockScheduler = dockScheduler ?? throw new ArgumentNullException(nameof(dockScheduler));
        _primaryDockBay = primaryDockBay;
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <summary>
    /// Project the current simulation state into a <see cref="SimulationSnapshotDto"/> without mutating
    /// any state (Req 9.3, 23.3). Reads the zones and lots from their repositories, the agents and
    /// starships from the tick-state seam, and the metrics + parameters from their read-only projections,
    /// then assembles the immutable DTO.
    /// </summary>
    /// <param name="ct">Cancellation token propagated to the repository / tick-state reads.</param>
    /// <returns>An immutable full-state snapshot reflecting the current simulation state.</returns>
    public async Task<SimulationSnapshotDto> HandleAsync(CancellationToken ct = default)
    {
        var zones = await _zones.ListAllAsync(ct).ConfigureAwait(false);
        var lots = await _lots.ListAllAsync(ct).ConfigureAwait(false);
        var tickState = await _tickState.GetTickStateAsync(ct).ConfigureAwait(false);

        // A null tick state (no spatial subsystem wired) contributes no agents or starships (Req: headless
        // deployments still return a valid snapshot). Otherwise project the live agents/starships.
        var agents = tickState is null
            ? Array.Empty<AgentDto>()
            : tickState.Agents.Select(a => ToAgentDto(tickState, a)).ToArray();

        var starships = tickState is null
            ? Array.Empty<StarshipDto>()
            : ToStarshipsDto(tickState);

        var zoneDtos = zones.Select(ToZoneDto).ToArray();
        var lotDtos = lots.Select(ToLotDto).ToArray();

        // Read-only projections: ToDto reads current values only and never mutates the components.
        var metrics = _metrics.ToDto(_dockScheduler, _primaryDockBay);
        var parameters = _parameters.ToDto();

        return new SimulationSnapshotDto(
            Zones: zoneDtos,
            Lots: lotDtos,
            Agents: agents,
            Starships: starships,
            Metrics: metrics,
            Parameters: parameters);
    }

    // ---- Pure domain → DTO projections (no mutation). ----

    private static TemperatureZoneDto ToZoneDto(TemperatureZone zone) => new(
        Id: zone.Id.Value,
        MinC: zone.AllowableRange.MinCelsius,
        MaxC: zone.AllowableRange.MaxCelsius,
        Capacity: zone.Capacity,
        Stored: zone.StoredQuantity);

    private static GelLotDto ToLotDto(GelLot lot) => new(
        Id: lot.Id.Value,
        GelTypeId: lot.GelTypeId.Value,
        ExpiresAt: lot.ExpiresAt,
        Quantity: lot.Quantity,
        IsExpired: lot.IsExpired,
        AtRisk: lot.AtRisk,
        ZoneId: lot.AssignedZoneId is { } zoneId ? zoneId.Value : null);

    private static AgentDto ToAgentDto(TickState tickState, Agent agent) => new(
        Id: agent.Id.Value,
        X: agent.Position.X,
        Y: agent.Position.Y,
        PathCells: agent.CurrentPath is { } path
            ? path.Cells.Select(c => new CellDto(c.X, c.Y)).ToArray()
            : Array.Empty<CellDto>(),
        CellsPerSecond: agent.CellsPerSecond,
        Phase: "Active",
        CarryingLotId: TryCarryingLotId(tickState, agent.Id));

    private static Guid? TryCarryingLotId(TickState tickState, AgentId agentId)
    {
        if (!tickState.AgentTasks.TryGetValue(agentId, out var taskId))
        {
            return null;
        }

        if (tickState.PutAwayTaskLotLinks.TryGetValue(taskId, out var lotId))
        {
            for (int i = 0; i < tickState.InTransitLotIds.Count; i++)
            {
                if (tickState.InTransitLotIds[i].Equals(lotId))
                {
                    return lotId.Value;
                }
            }

            return null;
        }

        if (tickState.PickTaskLotLinks.TryGetValue(taskId, out var pickLot))
        {
            for (int i = 0; i < tickState.InTransitLotIds.Count; i++)
            {
                if (tickState.InTransitLotIds[i].Equals(pickLot))
                {
                    return pickLot.Value;
                }
            }

            return null;
        }

        return null;
    }

    private static StarshipDto ToStarshipDto(Starship starship, int dockIndex, string phase) => new(
        Id: starship.Id.Value,
        Capacity: starship.CargoCapacity,
        Loaded: starship.LoadedQuantity,
        DestinationColony: starship.Destination.Value,
        Windows: starship.Windows.Select(w => new LoadingWindowDto(w.Start, w.End)).ToArray(),
        Phase: phase,
        DockIndex: dockIndex);

    private static IReadOnlyList<StarshipDto> ToStarshipsDto(TickState tickState)
    {
        return tickState.Starships
            .OrderBy(s => s.Id)
            .Select(ship =>
            {
                tickState.StarshipRuntimes.TryGetValue(ship.Id, out var rt);
                return ToStarshipDto(
                    ship,
                    rt?.DockIndex ?? -1,
                    rt?.Phase ?? StarshipPhases.Away);
            })
            .ToArray();
    }
}
