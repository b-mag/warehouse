using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Forge.Infrastructure.Persistence;

/// <summary>
/// Design-time factory that lets the EF Core tooling (<c>dotnet ef migrations add</c>) construct a
/// <see cref="ForgeDbContext"/> without the Api composition root (task 28.2, Req 26.2). At runtime the
/// context is created from DI with the connection string supplied by <see cref="IEmbeddedDatabaseHost"/>;
/// the tooling, however, has no host, so this factory hands it a context bound to the Npgsql provider
/// with a placeholder local connection string.
/// <para>
/// Generating a migration only requires the provider + the model — <b>no live database is contacted</b> —
/// so the placeholder connection string is never opened. Keeping this factory in Infrastructure means the
/// migration scaffolds against the exact same provider and model the runtime uses.
/// </para>
/// </summary>
public sealed class ForgeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ForgeDbContext>
{
    // Placeholder Npgsql connection string used only so the provider can build the model at design time.
    // Migration scaffolding does not open a connection, so no Postgres needs to be running.
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=forge;Username=forge;Password=forge";

    /// <inheritdoc />
    public ForgeDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ForgeDbContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;

        return new ForgeDbContext(options);
    }
}
