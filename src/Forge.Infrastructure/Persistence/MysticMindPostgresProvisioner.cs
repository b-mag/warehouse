using System.Globalization;
using MysticMind.PostgresEmbed;
using Npgsql;

namespace Forge.Infrastructure.Persistence;

/// <summary>
/// The concrete <see cref="IEmbeddedPostgresProvisioner"/> (Req 26.1). It runs a fully self-managed
/// local Postgres server via <see cref="PgServer"/> (MysticMind.PostgresEmbed, backed by the zonky
/// minimal embedded-postgres binaries) so the app needs <b>no</b> external database setup — the very
/// first run downloads the ~10 MB binaries once, then every run reuses them.
/// <para>
/// The instance is <b>persistent</b>, not ephemeral: it uses a fixed <see cref="InstanceId"/> and a
/// fixed working directory under <c>%LOCALAPPDATA%\Forge\pg</c> (<see cref="SpecialFolder.LocalApplicationData"/>),
/// with <c>clearInstanceDirOnStop</c> and <c>clearWorkingDirOnStart</c> both <c>false</c>, so the
/// database and its data survive process restarts (this is a real dev DB). Reusing an existing named
/// instance directory makes the library skip the whole download/extract/initdb setup and simply start
/// the already-provisioned server.
/// </para>
/// <para>
/// The embedded server always exposes a <c>postgres</c> superuser with trust authentication (any
/// password is accepted). This provisioner ensures a dedicated <c>forge</c> database exists (creating
/// it on first run) and returns the Npgsql connection string pointing at it. Per the library's
/// documented Npgsql guidance, the connection string sets <c>Pooling=false</c> to avoid the
/// "connection forcibly closed" transport error.
/// </para>
/// </summary>
public sealed class MysticMindPostgresProvisioner : IEmbeddedPostgresProvisioner
{
    /// <summary>
    /// The pinned Postgres binary version. 15.3.0 is the version documented as known-good by the
    /// MysticMind.PostgresEmbed library and zonky publishes Windows-x64 binaries for it, so it is the
    /// safest choice for a first-path embedded DB.
    /// </summary>
    private const string PostgresVersion = "15.3.0";

    /// <summary>The application database this provisioner ensures exists and hands the DbContext.</summary>
    private const string ForgeDatabaseName = "forge";

    /// <summary>The default embedded superuser (trust auth — any password is accepted).</summary>
    private const string SuperUser = "postgres";

    /// <summary>The default database that always exists on the embedded server (used to create <c>forge</c>).</summary>
    private const string DefaultDatabaseName = "postgres";

    // A fixed named-instance id so the library reuses the same provisioned instance directory across
    // runs (skipping the download/extract/initdb setup) instead of creating a fresh ephemeral one.
    private static readonly Guid InstanceId = new("6f9d0a5a-7b3e-4c21-9a2f-0c9d3e1b7a44");

    private readonly object _gate = new();
    private PgServer? _server;
    private string? _connectionString;

    /// <inheritdoc />
    public async Task<string> StartAsync(EmbeddedDatabaseOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Guard against a double-start: if we already have a running server, hand back its string.
        lock (_gate)
        {
            if (_server is not null && _connectionString is not null)
            {
                return _connectionString;
            }
        }

        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Forge",
            "pg");
        Directory.CreateDirectory(dbDir);

        // Honor a fixed port if the caller pinned one in the configured connection string; otherwise
        // pass 0 so PgServer picks a free port. addLocalUserAccessPermission avoids the documented
        // Windows initdb permission failure; the persistence flags keep the instance across runs.
        var port = TryGetConfiguredPort(options.ConnectionString);

        var server = new PgServer(
            PostgresVersion,
            pgUser: SuperUser,
            dbDir: dbDir,
            instanceId: InstanceId,
            port: port,
            addLocalUserAccessPermission: OperatingSystem.IsWindows(),
            clearInstanceDirOnStop: false,
            clearWorkingDirOnStart: false);

        try
        {
            await server.StartAsync(cancellationToken).ConfigureAwait(false);

            var connectionString = BuildConnectionString(server.PgPort, ForgeDatabaseName);

            // The embedded server's only database is `postgres`; ensure our `forge` database exists.
            await EnsureForgeDatabaseAsync(server.PgPort, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _server = server;
                _connectionString = connectionString;
            }

            return connectionString;
        }
        catch
        {
            // Best-effort cleanup so a failed start does not leave a half-started server holding the port.
            try
            {
                await server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore secondary teardown failures; surface the original start failure.
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        PgServer? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
            _connectionString = null;
        }

        if (server is null)
        {
            return;
        }

        await server.StopAsync(cancellationToken).ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
    }

    // Connect to the always-present `postgres` database and CREATE DATABASE forge if it is missing.
    // CREATE DATABASE cannot run in a transaction, so it is issued as a plain command.
    private static async Task EnsureForgeDatabaseAsync(int port, CancellationToken cancellationToken)
    {
        var adminConnectionString = BuildConnectionString(port, DefaultDatabaseName);

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var check = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name", connection))
        {
            check.Parameters.AddWithValue("name", ForgeDatabaseName);
            var exists = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (exists is not null)
            {
                return;
            }
        }

        // The database name is a fixed constant (never user input), so quoting it inline is safe.
        await using var create = new NpgsqlCommand(
            $"CREATE DATABASE \"{ForgeDatabaseName}\"", connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Build the Npgsql connection string. Pooling=false per the library's documented Npgsql guidance
    // (avoids the sporadic "connection forcibly closed" transport error against the embedded server).
    private static string BuildConnectionString(int port, string database) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Host=localhost;Port={port};Database={database};Username={SuperUser};Password=postgres;Pooling=false");

    // If the caller pinned a port in the configured connection string, honor it; else 0 => free port.
    private static int TryGetConfiguredPort(string? configuredConnectionString)
    {
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return 0;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(configuredConnectionString);
            return builder.Port > 0 ? builder.Port : 0;
        }
        catch (ArgumentException)
        {
            // A malformed configured string just means "let PgServer pick a free port".
            return 0;
        }
    }
}
