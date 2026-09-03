using Forge.Application.Abstractions.Repositories;
using Forge.Domain.Common;
using Forge.Infrastructure.Persistence;
using Forge.Simulation;
using Forge.Simulation.Demand;
using Forge.Simulation.Temperature;
using Microsoft.EntityFrameworkCore;

namespace Forge.Api.Simulation;

/// <summary>
/// The composition-root implementation of the Simulation <see cref="ISimulationCatalogProvider"/>
/// seam (task 33.3). Each tick the Simulation loop reads this to learn <i>what</i> to generate
/// against — which gel types + dock bays inbound arrivals draw from, which colonies (with their
/// <see cref="Forge.Domain.Colonies.DemandProfile"/>) place orders, and which lots (with their zone
/// bands) receive temperature readings. This provider projects the <b>live seeded warehouse state</b>
/// into those catalogs, which is what turns the seeded warehouse into ongoing activity.
/// <para>
/// <b>Why it lives in the Api and not Infrastructure.</b> The <see cref="ISimulationCatalogProvider"/>
/// interface and its catalog record types (<see cref="ColonyDemandSource"/>,
/// <see cref="TemperatureReadingTarget"/>) live in <c>Forge.Simulation</c>, which Infrastructure
/// deliberately does not reference (the layer boundary: Infrastructure → Application/Domain/Contracts
/// only, task 1.1). The Api references both Infrastructure and Simulation, so the projection that reads
/// the seeded persistence state (Infrastructure) into the Simulation catalog shape lives here.
/// </para>
/// <para>
/// <b>Lifetime &amp; scoping.</b> Registered as a singleton (the Simulation generators capture the
/// catalog once at construction). The seeded state lives in the scoped <see cref="ForgeDbContext"/>, so
/// the provider reads it through an <see cref="IServiceScopeFactory"/> — never capturing a scoped
/// context. The catalog getters are synchronous (the loop reads them inline), so the provider serves
/// cached snapshots that a background <see cref="RefreshAsync"/> repopulates: the mostly-static gel
/// types / colonies are loaded once, while the lot-derived temperature targets are refreshed each run so
/// lots created by arrivals become temperature-reading targets on a later tick (Req 6.2).
/// </para>
/// <para>
/// <b>Determinism / stable ordering.</b> Every returned list is ordered by ascending id so the seeded
/// generators consume their PRNG streams reproducibly (Req 12.7). Snapshots are swapped atomically via
/// volatile references so a getter never observes a torn list.
/// </para>
/// </summary>
public sealed class SeededSimulationCatalogProvider : ISimulationCatalogProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    // The synthesized dock bays (the seeder persists none). Fixed for the process lifetime and ordered.
    private readonly IReadOnlyList<DockBayId> _dockBays;

    // Volatile snapshot references, swapped atomically by RefreshAsync so the sync getters are lock-free.
    private volatile IReadOnlyList<GelTypeId> _gelTypes = Array.Empty<GelTypeId>();
    private volatile IReadOnlyList<ColonyDemandSource> _colonies = Array.Empty<ColonyDemandSource>();
    private volatile IReadOnlyList<TemperatureReadingTarget> _temperatureTargets = Array.Empty<TemperatureReadingTarget>();

    private bool _staticLoaded;

    /// <summary>
    /// Create the provider over the scope factory it reads seeded state through and the synthesized
    /// dock bays arrivals are received at.
    /// </summary>
    /// <param name="scopeFactory">Opens a fresh DI scope per read so no scoped context is captured.</param>
    /// <param name="dockBays">The stable, deterministic dock-bay ids (also registered with the scheduler).</param>
    public SeededSimulationCatalogProvider(IServiceScopeFactory scopeFactory, IReadOnlyList<DockBayId> dockBays)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(dockBays);
        _dockBays = dockBays.OrderBy(b => b).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<GelTypeId> GelTypes => _gelTypes;

    /// <inheritdoc />
    public IReadOnlyList<DockBayId> DockBays => _dockBays;

    /// <inheritdoc />
    public IReadOnlyList<ColonyDemandSource> Colonies => _colonies;

    /// <inheritdoc />
    public IReadOnlyList<TemperatureReadingTarget> TemperatureTargets => _temperatureTargets;

    /// <summary>
    /// Refresh the projected catalogs from the live seeded state (task 33.3). The mostly-static gel
    /// types + colonies are loaded once (the seed is fixed reference data); the lot-derived temperature
    /// targets are re-read every call so newly created lots become temperature-reading targets. Reads
    /// run in a fresh DI scope so no scoped <see cref="ForgeDbContext"/> is captured by this singleton.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        if (!_staticLoaded)
        {
            await LoadStaticCatalogsAsync(scope.ServiceProvider, context, ct).ConfigureAwait(false);
            _staticLoaded = true;
        }

        _temperatureTargets = await LoadTemperatureTargetsAsync(context, ct).ConfigureAwait(false);
    }

    // Gel types + colonies are fixed seeded reference data; load them once, in ascending-id order.
    private async Task LoadStaticCatalogsAsync(
        IServiceProvider services, ForgeDbContext context, CancellationToken ct)
    {
        var gelTypeIds = await context.GelTypes
            .AsNoTracking()
            .Select(g => g.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        gelTypeIds.Sort();
        _gelTypes = gelTypeIds;

        var colonies = await services.GetRequiredService<IColonyRepository>()
            .ListAllAsync(ct)
            .ConfigureAwait(false);

        _colonies = colonies
            .OrderBy(c => c.Id)
            .Select(c => new ColonyDemandSource(c.Id, c.Profile))
            .ToArray();
    }

    // Temperature targets are the lots that currently sit in a zone, paired with that zone's allowable
    // band. Re-read each refresh so arrivals-created lots become reading targets on a later tick.
    private static async Task<IReadOnlyList<TemperatureReadingTarget>> LoadTemperatureTargetsAsync(
        ForgeDbContext context, CancellationToken ct)
    {
        var zones = await context.TemperatureZones
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var bandByZone = zones.ToDictionary(z => z.Id, z => z.AllowableRange);

        var lots = await context.GelLots
            .AsNoTracking()
            .Where(l => l.AssignedZoneId != null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var targets = new List<TemperatureReadingTarget>(lots.Count);
        foreach (var lot in lots.OrderBy(l => l.Id))
        {
            if (lot.AssignedZoneId is { } zoneId && bandByZone.TryGetValue(zoneId, out var band))
            {
                targets.Add(new TemperatureReadingTarget(lot.Id, band));
            }
        }

        return targets;
    }
}
