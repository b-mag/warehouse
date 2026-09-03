using Forge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Startup-migration integration tests for the persistence bootstrap (task 28.3, Req 26.2, 26.5, 28.2).
/// <para>
/// Two concerns are exercised here:
/// </para>
/// <list type="number">
///   <item><description>
///     <b>Apply migrations to an empty database (Req 26.2).</b> When a real Postgres is reachable â€” via
///     the <c>FORGE_TEST_POSTGRES</c> environment variable holding an Npgsql connection string â€” this
///     opens a fresh <see cref="ForgeDbContext"/>, runs <see cref="RelationalDatabaseFacadeExtensions.Migrate"/>
///     against the empty database, and asserts the <c>InitialCreate</c> schema is materialized (the
///     mapped aggregate tables exist and every generated migration is recorded as applied). When no
///     Postgres is present the test SKIPS (not fails) so the suite stays green in environments without a
///     database.
///   </description></item>
///   <item><description>
///     <b>Descriptive failure when the database cannot be initialized (Req 26.5).</b> This portion has no
///     external dependency and runs everywhere: it asserts the <see cref="EmbeddedDatabaseHost"/> bootstrap
///     surfaces an <see cref="EmbeddedDatabaseHostException"/> carrying a descriptive message (and the
///     underlying cause) when provisioning cannot complete, which is what the Api startup path relies on to
///     fail startup descriptively.
///   </description></item>
/// </list>
/// </summary>
public sealed class StartupMigrationIntegrationTests
{
    /// <summary>
    /// Environment variable holding an Npgsql connection string to a disposable, empty Postgres database.
    /// When unset, the live-Postgres migration test skips so the suite passes without a database.
    /// </summary>
    private const string PostgresConnectionEnvVar = "FORGE_TEST_POSTGRES";

    /// <summary>
    /// The aggregate tables the <c>InitialCreate</c> migration must create. Matched case-insensitively
    /// against <c>information_schema.tables</c> so the assertion is robust to the mapped snake_case names.
    /// </summary>
    private static readonly string[] ExpectedTables =
    [
        "colonies",
        "colony_orders",
        "gel_types",
        "gel_lots",
        "temperature_zones",
        "starships",
        "workers",
        "dock_bays",
        "pick_faces",
        "warehouse_tasks",
    ];

    private readonly ITestOutputHelper _output;

    public StartupMigrationIntegrationTests(ITestOutputHelper output) => _output = output;

    private static DbContextOptions<ForgeDbContext> BuildNpgsqlOptions(string connectionString) =>
        new DbContextOptionsBuilder<ForgeDbContext>()
            .UseNpgsql(connectionString)
            .Options;

    /// <summary>
    /// Req 26.2 (guarded): applying migrations to an empty database materializes the full schema. Runs only
    /// when a Postgres connection string is supplied via <c>FORGE_TEST_POSTGRES</c>; otherwise skips so the
    /// suite is green in environments without a database (do NOT require a running Postgres for the suite).
    /// </summary>
    [Fact]
    public async Task Migrate_on_an_empty_database_materializes_the_initial_schema()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // No Postgres available in this environment: treat as a pass-through (xUnit v2 has no
            // dynamic Assert.Skip). Set FORGE_TEST_POSTGRES to an Npgsql connection string pointing at a
            // disposable, empty database to run this live startup-migration test (Req 26.2). The always-on
            // descriptive-failure tests below (Req 26.5) still exercise the bootstrap in every environment.
            _output.WriteLine(
                $"Skipped: no Postgres available. Set {PostgresConnectionEnvVar} to an Npgsql connection "
                + "string pointing at a disposable, empty database to run the live startup-migration test.");
            return;
        }

        await using var context = new ForgeDbContext(BuildNpgsqlOptions(connectionString));

        // Start from a clean slate so we genuinely apply migrations to an EMPTY database (Req 26.2),
        // then always tear the schema back down so the test is repeatable against the same instance.
        await context.Database.EnsureDeletedAsync();
        try
        {
            // Req 26.2: startup applies EF Core migrations to the (empty) database.
            await context.Database.MigrateAsync();

            // Every generated migration is now recorded as applied (nothing left pending).
            var pending = await context.Database.GetPendingMigrationsAsync();
            Assert.Empty(pending);

            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.NotEmpty(applied);
            Assert.Contains(applied, m => m.EndsWith("InitialCreate", StringComparison.Ordinal));

            // The mapped aggregate tables exist in the public schema â€” the schema was actually materialized.
            foreach (var table in ExpectedTables)
            {
                var exists = await TableExistsAsync(context, table);
                Assert.True(exists, $"Expected table '{table}' to exist after applying migrations.");
            }
        }
        finally
        {
            await context.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<bool> TableExistsAsync(ForgeDbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables "
            + "WHERE table_schema = 'public' AND lower(table_name) = lower(@name));";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync();
        return result is bool b && b;
    }

    /// <summary>
    /// Req 26.5 (always runs): when embedded mode is selected but provisioning cannot complete, the host
    /// fails with a descriptive <see cref="EmbeddedDatabaseHostException"/> whose message names the cause and
    /// whose <see cref="Exception.InnerException"/> preserves the underlying failure â€” this is what lets the
    /// Api startup path fail with a descriptive error rather than a bare crash.
    /// </summary>
    [Fact]
    public async Task Startup_fails_with_a_descriptive_error_when_the_database_cannot_initialize()
    {
        var host = new EmbeddedDatabaseHost(
            new EmbeddedDatabaseOptions { Embedded = true },
            new UnavailableProvisioner());

        var ex = await Assert.ThrowsAsync<EmbeddedDatabaseHostException>(() => host.StartAsync());

        // Descriptive: the message explains the store could not be brought up, and the root cause is kept.
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Contains("could not be", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    /// <summary>
    /// Req 26.5 (always runs): the misconfiguration where embedded mode is on but no provisioner exists to
    /// bring the database up also fails descriptively (the message points the operator at the fix).
    /// </summary>
    [Fact]
    public async Task Startup_fails_descriptively_when_embedded_mode_has_no_provisioner_to_initialize_the_database()
    {
        var host = new EmbeddedDatabaseHost(new EmbeddedDatabaseOptions { Embedded = true });

        var ex = await Assert.ThrowsAsync<EmbeddedDatabaseHostException>(() => host.StartAsync());

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Contains("provisioner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A provisioner that always fails to bring up the embedded instance, standing in for an environment
    /// where the embedded Postgres cannot be initialized (Req 26.5). The host wraps this into a descriptive
    /// <see cref="EmbeddedDatabaseHostException"/>.
    /// </summary>
    private sealed class UnavailableProvisioner : IEmbeddedPostgresProvisioner
    {
        public Task<string> StartAsync(EmbeddedDatabaseOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("embedded Postgres binary is not available in this environment");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
