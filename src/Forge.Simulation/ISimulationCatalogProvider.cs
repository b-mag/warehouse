using Forge.Domain.Common;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;

namespace Forge.Simulation;

/// <summary>
/// The <b>catalog seam</b> the Simulation tick loop reads on each tick to learn <i>what</i> to
/// generate against: which gel types and dock bays inbound arrivals draw from, which colonies (with
/// their <see cref="Forge.Domain.Colonies.DemandProfile"/>) place orders, and which lots (with their
/// assigned zone bands) receive temperature readings.
/// <para>
/// This is a deliberate seam, not a hard-coded catalog. The runtime catalog is a function of the
/// seeded warehouse state (1000 gel types, 2–20 zones, 3–5 colonies, 1000–100000 lots — Req 25/26)
/// which is produced by Infrastructure seeding / read from the WMS Core. Those tasks (Infrastructure
/// and the Api composition root) supply a real <see cref="ISimulationCatalogProvider"/> that projects
/// live seeded state into these catalogs; the Simulation project only depends on this abstraction so
/// it stays decoupled from persistence and seeding.
/// </para>
/// <para>
/// <b>Determinism / stable ordering.</b> Every returned list MUST be in a stable order (e.g. ascending
/// id) so the seeded generators consume their PRNG streams reproducibly (Req 12.7). The lists may grow
/// over simulated time (e.g. lots created by arrivals become temperature-reading targets on a later
/// tick); the loop re-reads the provider each tick to pick up such changes.
/// </para>
/// </summary>
public interface ISimulationCatalogProvider
{
    /// <summary>
    /// The gel types inbound arrivals may be received as, in a stable order. Non-empty while the
    /// simulation is running; an empty catalog produces no arrivals for the tick.
    /// </summary>
    IReadOnlyList<GelTypeId> GelTypes { get; }

    /// <summary>
    /// The dock bays inbound arrivals may be received at, in a stable order. Non-empty while the
    /// simulation is running; an empty catalog produces no arrivals for the tick.
    /// </summary>
    IReadOnlyList<DockBayId> DockBays { get; }

    /// <summary>
    /// The colonies (each paired with its <see cref="Forge.Domain.Colonies.DemandProfile"/>) that place
    /// authoritative orders, in a stable order (Req 12.1, 12.2). An empty catalog produces no demand.
    /// </summary>
    IReadOnlyList<ColonyDemandSource> Colonies { get; }

    /// <summary>
    /// The lots (each paired with its assigned zone's allowable band) that receive temperature
    /// readings this tick, in a stable order (Req 6.2). An empty catalog produces no readings.
    /// </summary>
    IReadOnlyList<TemperatureReadingTarget> TemperatureTargets { get; }
}
