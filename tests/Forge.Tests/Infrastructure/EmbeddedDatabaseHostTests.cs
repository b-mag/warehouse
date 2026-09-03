using Forge.Infrastructure.Persistence;
using Xunit;

namespace Forge.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="EmbeddedDatabaseHost"/> (task 28.1, Req 26.1, 26.5). The host normalizes the
/// embedded-vs-container choice to a single Npgsql connection string; container mode attaches to an
/// external string without lifecycle work, and embedded mode delegates provisioning to an
/// <see cref="IEmbeddedPostgresProvisioner"/>. These tests need no live Postgres.
/// </summary>
public sealed class EmbeddedDatabaseHostTests
{
    private sealed class FakeProvisioner : IEmbeddedPostgresProvisioner
    {
        private readonly string _connectionString;

        public FakeProvisioner(string connectionString) => _connectionString = connectionString;

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task<string> StartAsync(EmbeddedDatabaseOptions options, CancellationToken cancellationToken)
        {
            StartCount++;
            return Task.FromResult(_connectionString);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingProvisioner : IEmbeddedPostgresProvisioner
    {
        public Task<string> StartAsync(EmbeddedDatabaseOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task Container_mode_attaches_to_the_external_connection_string()
    {
        var options = new EmbeddedDatabaseOptions
        {
            Embedded = false,
            ConnectionString = "Host=db;Port=5432;Database=forge;Username=u;Password=p",
        };
        await using var host = new EmbeddedDatabaseHost(options);

        await host.StartAsync();

        Assert.False(host.IsEmbedded);
        Assert.Equal(options.ConnectionString, host.ConnectionString);
    }

    [Fact]
    public async Task Embedded_mode_uses_the_provisioner_connection_string()
    {
        var provisioner = new FakeProvisioner("Host=127.0.0.1;Port=6543;Database=embedded;Username=e;Password=e");
        var options = new EmbeddedDatabaseOptions { Embedded = true };
        await using var host = new EmbeddedDatabaseHost(options, provisioner);

        await host.StartAsync();

        Assert.True(host.IsEmbedded);
        Assert.Equal("Host=127.0.0.1;Port=6543;Database=embedded;Username=e;Password=e", host.ConnectionString);
        Assert.Equal(1, provisioner.StartCount);
    }

    [Fact]
    public void ConnectionString_before_start_throws()
    {
        var host = new EmbeddedDatabaseHost(new EmbeddedDatabaseOptions { Embedded = false });

        Assert.Throws<InvalidOperationException>(() => host.ConnectionString);
    }

    [Fact]
    public async Task Embedded_mode_without_a_provisioner_fails_with_a_descriptive_error()
    {
        var host = new EmbeddedDatabaseHost(new EmbeddedDatabaseOptions { Embedded = true });

        var ex = await Assert.ThrowsAsync<EmbeddedDatabaseHostException>(() => host.StartAsync());
        Assert.Contains("provisioner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failing_provisioner_is_wrapped_in_a_descriptive_startup_error()
    {
        var host = new EmbeddedDatabaseHost(new EmbeddedDatabaseOptions { Embedded = true }, new ThrowingProvisioner());

        var ex = await Assert.ThrowsAsync<EmbeddedDatabaseHostException>(() => host.StartAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("could not be", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_is_idempotent_and_stop_tears_down_the_embedded_instance()
    {
        var provisioner = new FakeProvisioner("Host=127.0.0.1;Port=6543;Database=embedded;Username=e;Password=e");
        var host = new EmbeddedDatabaseHost(new EmbeddedDatabaseOptions { Embedded = true }, provisioner);

        await host.StartAsync();
        await host.StartAsync();
        Assert.Equal(1, provisioner.StartCount);

        await host.StopAsync();
        Assert.Equal(1, provisioner.StopCount);
    }
}
