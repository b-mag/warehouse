using Forge.Application.Abstractions;
using Forge.Application.Simulation;
using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Spatial;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// The composition-root implementation of the Application <see cref="IReservationManager"/> seam
/// (task 33.3; Req 19.1–19.6). It composes the two domain-pure reservation primitives — the
/// <see cref="ReservationLedger"/> (path-segment mutual exclusion, Req 19.1–19.3) and the
/// <see cref="SingleOccupancyRegistry"/> (dock-bay / pick-face single occupancy + FIFO queueing,
/// Req 19.4) — behind the single seam the core's movement stage depends on. Congestion is derived
/// from those same held reservations + queued agents via <see cref="WarehouseMetrics.GetCongestion"/>
/// (Req 19.5), mirroring the metrics component's projection so both surfaces agree.
/// <para>
/// <b>Lifetime &amp; state.</b> This holds the live reservation state, so it is registered as a
/// singleton — a single shared grant point across the whole engine (Req 19.1: the ledger is the one
/// place a reservation is accepted or rejected). The tick loop's movement stage acquires and releases
/// reservations through it each tick.
/// </para>
/// <para>
/// <b>Thread-safety.</b> The domain ledger/registry are not themselves synchronized, and the singleton
/// may be touched by the tick loop and by a congestion read from a snapshot query concurrently, so
/// every operation is guarded by a single lock. The critical sections are tiny (a bounded scan of the
/// held reservations), so this never back-pressures the tick loop meaningfully.
/// </para>
/// <para>
/// <b>Congestion resources.</b> Congestion counts the agents queued on the single-occupancy resources
/// this manager currently knows about — the resources any agent has attempted to acquire since start.
/// The set is accumulated as <see cref="TryAcquire"/> is called so the congestion projection always
/// covers exactly the resources in play, without the caller having to enumerate a resource catalog.
/// </para>
/// </summary>
public sealed class ReservationManager : IReservationManager
{
    private readonly object _gate = new();
    private readonly ReservationLedger _ledger = new();
    private readonly SingleOccupancyRegistry _registry = new();

    // The single-occupancy resources this manager has seen, so GetCongestion can count queued agents
    // across all of them without the caller supplying a catalog. A SortedSet keeps the enumeration
    // order deterministic (SingleOccupancyResourceId is totally ordered).
    private readonly SortedSet<SingleOccupancyResourceId> _knownResources = new();

    /// <inheritdoc />
    public ReservationOutcome TryReserve(AgentId agent, IReadOnlyList<TimedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        lock (_gate)
        {
            return _ledger.TryReserve(agent, segments);
        }
    }

    /// <inheritdoc />
    public ResourceOutcome TryAcquire(AgentId agent, SingleOccupancyResourceId resource)
    {
        lock (_gate)
        {
            _knownResources.Add(resource);
            return _registry.TryAcquire(agent, resource);
        }
    }

    /// <inheritdoc />
    public void Release(AgentId agent)
    {
        lock (_gate)
        {
            _ledger.Release(agent);

            // The registry has no bulk "release everything held by this agent" op; abandon the agent on
            // every known resource so it stops holding/queuing anywhere. Abandon is a no-op where the
            // agent neither holds nor waits, so scanning all known resources is safe and deterministic.
            foreach (var resource in _knownResources)
            {
                _registry.Abandon(agent, resource);
            }
        }
    }

    /// <inheritdoc />
    public CongestionSnapshot GetCongestion()
    {
        lock (_gate)
        {
            var resources = new List<SingleOccupancyResourceId>(_knownResources);
            return WarehouseMetrics.GetCongestion(_ledger, _registry, resources);
        }
    }
}
