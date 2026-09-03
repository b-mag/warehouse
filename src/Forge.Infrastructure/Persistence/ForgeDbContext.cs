using Forge.Domain.ColdChain;
using Forge.Domain.Colonies;
using Forge.Domain.Docks;
using Forge.Domain.Gels;
using Forge.Domain.Labor;
using Forge.Domain.Tasks;
using Forge.Domain.Vessels;
using Microsoft.EntityFrameworkCore;

namespace Forge.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit-of-work / persistence context for the WMS Core (Req 26.1). It maps the domain
/// aggregates via <see cref="IEntityTypeConfiguration{TEntity}"/> classes discovered in this assembly,
/// so all persistence concerns (keys, value converters for strongly-typed ids and value objects, owned
/// types, column facets) live entirely in Infrastructure — the Domain stays persistence-ignorant.
/// <para>
/// The Npgsql provider backs this context for both embedded and container Postgres; only the connection
/// string differs (design "Persistence and Seeding"), supplied by <see cref="IEmbeddedDatabaseHost"/> at
/// composition time. This class deliberately contains <b>no</b> migrations and <b>no</b> repository /
/// unit-of-work interface implementations — those are task 28.2. Its scope is the model + configurations.
/// </para>
/// </summary>
public sealed class ForgeDbContext : DbContext
{
    /// <summary>Construct the context from options (provider + connection string) wired at composition time.</summary>
    public ForgeDbContext(DbContextOptions<ForgeDbContext> options)
        : base(options)
    {
    }

    /// <summary>Gel types (formulation families) (Req 3.2).</summary>
    public DbSet<GelType> GelTypes => Set<GelType>();

    /// <summary>Produced gel lots (batches) with expiry, quantity, and temperature history (Req 3.1).</summary>
    public DbSet<GelLot> GelLots => Set<GelLot>();

    /// <summary>Temperature-controlled storage zones (Req 6.1).</summary>
    public DbSet<TemperatureZone> TemperatureZones => Set<TemperatureZone>();

    /// <summary>Colonies the warehouse supplies (Req 12.1).</summary>
    public DbSet<Colony> Colonies => Set<Colony>();

    /// <summary>Colony orders (Req 12.1).</summary>
    public DbSet<ColonyOrder> ColonyOrders => Set<ColonyOrder>();

    /// <summary>Starships (transport vessels) with cargo capacity and loading windows (Req 13.1).</summary>
    public DbSet<Starship> Starships => Set<Starship>();

    /// <summary>Labor workers with hourly rate and shifts (Req 15.1).</summary>
    public DbSet<Worker> Workers => Set<Worker>();

    /// <summary>Dock bays (single-occupancy inbound/outbound resources) (Req 17.1).</summary>
    public DbSet<DockBay> DockBays => Set<DockBay>();

    /// <summary>Pick faces (single-occupancy storage-front resources) (Req 19.4).</summary>
    public DbSet<PickFace> PickFaces => Set<PickFace>();

    /// <summary>Warehouse tasks (units of work) (Req 8.1).</summary>
    public DbSet<WarehouseTask> WarehouseTasks => Set<WarehouseTask>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Apply every IEntityTypeConfiguration<T> defined in this assembly so each aggregate's mapping
        // (keys, strongly-typed-id / value-object converters, owned types) lives in its own configuration
        // class rather than inline here (Req 26.1). This keeps the Domain free of persistence attributes.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ForgeDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
