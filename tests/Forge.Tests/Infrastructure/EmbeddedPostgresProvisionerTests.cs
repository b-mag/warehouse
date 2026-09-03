using Forge.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Opt-in integration smoke test for <see cref="MysticMindPostgresProvisioner"/> (Req 26.1). This boots
/// a REAL self-managed embedded Postgres, which on a cold machine downloads ~10 MB of binaries and runs
/// initdb, so it is slow. It is therefore GUARDED behind the <c>FORGE_RUN_EMBEDDED_PG</c> environment
/// variable: with the variable unset (the default, including CI) the test early-returns as a no-op so it
/// never slows the suite or requires a live server. Set <c>FORGE_RUN_EMBEDDED_PG=1</c> to run it on
/// demand (xUnit v2 has no <c>Assert.Skip</c>, hence the early-return pattern).
/// </summary>
public sealed class EmbeddedPostgresProvisionerTests
{
    private const string RunGate = "FORGE_RUN_EMBEDDED_PG";

    [Fact]
    public async Task Provisioner_starts_a_real_embedded_postgres_and_exposes_a_usable_forge_database()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RunGate)))
        {
            // Not opted in: pass as a no-op so the default/CI suite never boots an embedded server.
            return;
        }

        var provisioner = new MysticMindPostgresProvisioner();
        var options = new EmbeddedDatabaseOptions { Embedded = true };

        var connectionString = await provisioner.StartAsync(options, CancellationToken.None);

        try
        {
            Assert.Contains("Database=forge", connectionString, StringComparison.Ordinal);
            Assert.Contains("Pooling=false", connectionString, StringComparison.Ordinal);

            // Prove the returned string points at a live, usable `forge` database.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = new NpgsqlCommand("SELECT current_database()", connection);
            var currentDatabase = (string?)await command.ExecuteScalarAsync();

            Assert.Equal("forge", currentDatabase);
        }
        finally
        {
            await provisioner.StopAsync(CancellationToken.None);
        }
    }
}
