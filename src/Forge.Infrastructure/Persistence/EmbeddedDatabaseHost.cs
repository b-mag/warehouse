namespace Forge.Infrastructure.Persistence;

/// <summary>
/// Configuration for the <see cref="EmbeddedDatabaseHost"/> (Req 26.1). Carries the Npgsql connection
/// string the <see cref="ForgeDbContext"/> binds to and a flag selecting embedded vs container mode.
/// Per the design, embedded and container Postgres are distinguished <b>by connection string only</b>,
/// so this options object is the single place that choice is expressed.
/// </summary>
public sealed class EmbeddedDatabaseOptions
{
    /// <summary>
    /// The Npgsql connection string. In container mode this points at an externally-managed Postgres
    /// (e.g. the Docker Compose service, Req 26.4). In embedded mode it points at the locally-managed
    /// instance the provisioner brings up (defaulting to a local loopback instance).
    /// </summary>
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5432;Database=forge;Username=forge;Password=forge";

    /// <summary>
    /// Whether the host should provision and manage a local embedded Postgres instance (<c>true</c>,
    /// the default per Req 26.1) or simply attach to an externally-provided connection string such as
    /// a container-hosted Postgres (<c>false</c>).
    /// </summary>
    public bool Embedded { get; set; } = true;
}

/// <summary>
/// Delegate that provisions (starts) and de-provisions (stops) a local embedded Postgres instance for
/// the <see cref="EmbeddedDatabaseHost"/>. This is the injectable seam that a concrete embedded-Postgres
/// package (the <c>EmbeddedPostgres</c> / <c>PgServer</c> family named in the design's research notes)
/// plugs into: <see cref="StartAsync"/> brings the server up and returns the effective connection string
/// to bind to; <see cref="StopAsync"/> tears it down. Keeping provisioning behind this delegate lets the
/// embedded backend be swapped without touching the DbContext or the host's own lifecycle logic, and
/// lets tests exercise the host without a live server.
/// </summary>
public interface IEmbeddedPostgresProvisioner
{
    /// <summary>
    /// Bring up the embedded instance and return the connection string to bind to. Throws when the
    /// instance cannot be provisioned; the host wraps that in an
    /// <see cref="EmbeddedDatabaseHostException"/> so the Api startup path can fail with a descriptive
    /// error (Req 26.5).
    /// </summary>
    Task<string> StartAsync(EmbeddedDatabaseOptions options, CancellationToken cancellationToken);

    /// <summary>Tear down the embedded instance previously started by <see cref="StartAsync"/>.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IEmbeddedDatabaseHost"/> (Req 26.1). It normalizes the embedded-vs-container
/// choice down to a single Npgsql connection string (design "Persistence and Seeding"):
/// <list type="bullet">
///   <item><description>
///     In <b>container</b> mode (<see cref="EmbeddedDatabaseOptions.Embedded"/> is <c>false</c>) it does
///     no lifecycle work — it simply exposes the externally-provided connection string.
///   </description></item>
///   <item><description>
///     In <b>embedded</b> mode it delegates to an <see cref="IEmbeddedPostgresProvisioner"/> to start a
///     local instance and adopts the connection string that provisioner returns.
///   </description></item>
/// </list>
/// Either way the exposed <see cref="ConnectionString"/> is a plain Npgsql connection string, so the EF
/// Core provider path is identical for both. If startup fails, it throws
/// <see cref="EmbeddedDatabaseHostException"/> with a descriptive message so the Api startup path can
/// surface Req 26.5.
/// </summary>
public sealed class EmbeddedDatabaseHost : IEmbeddedDatabaseHost, IDisposable
{
    private readonly EmbeddedDatabaseOptions _options;
    private readonly IEmbeddedPostgresProvisioner? _provisioner;
    private string? _connectionString;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Create a host from its <paramref name="options"/>. When <paramref name="options"/> selects
    /// embedded mode, a <paramref name="provisioner"/> must be supplied to bring up the local instance;
    /// in container mode the provisioner is unused and may be <c>null</c>.
    /// </summary>
    public EmbeddedDatabaseHost(
        EmbeddedDatabaseOptions options,
        IEmbeddedPostgresProvisioner? provisioner = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _provisioner = provisioner;
    }

    /// <inheritdoc />
    public bool IsEmbedded => _options.Embedded;

    /// <inheritdoc />
    public string ConnectionString =>
        _connectionString
        ?? throw new InvalidOperationException(
            "The database host has not been started; call StartAsync before reading ConnectionString.");

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Idempotent: a second start after success is a no-op (Req 26.1).
        if (_started)
        {
            return;
        }

        try
        {
            if (_options.Embedded)
            {
                if (_provisioner is null)
                {
                    // Embedded mode requires a provisioner to bring up the local instance. Without one
                    // there is nothing to attach to, so fail with a descriptive error (Req 26.5).
                    throw new EmbeddedDatabaseHostException(
                        "Embedded database mode is enabled but no embedded Postgres provisioner was "
                        + "supplied; register an IEmbeddedPostgresProvisioner or switch to container "
                        + "mode by providing an external connection string.");
                }

                _connectionString = await _provisioner
                    .StartAsync(_options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Container mode: attach to the externally-managed connection string as-is.
                if (string.IsNullOrWhiteSpace(_options.ConnectionString))
                {
                    throw new EmbeddedDatabaseHostException(
                        "Container database mode is enabled but no connection string was provided.");
                }

                _connectionString = _options.ConnectionString;
            }

            _started = true;
        }
        catch (EmbeddedDatabaseHostException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Wrap any provisioning failure so the Api startup path fails with a descriptive error (Req 26.5).
            throw new EmbeddedDatabaseHostException(
                $"The {(_options.Embedded ? "embedded" : "container")} Postgres database could not be "
                + "initialized. See the inner exception for details.",
                ex);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        // Only embedded mode owns a lifecycle to tear down; container mode is externally managed.
        if (_options.Embedded && _provisioner is not null)
        {
            await _provisioner.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _started = false;
        _connectionString = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    /// <summary>
    /// Synchronous dispose. The host is also <see cref="IDisposable"/> (not only
    /// <see cref="IAsyncDisposable"/>) so the DI container can tear it down when a scope that captured
    /// it is disposed <b>synchronously</b> (e.g. <c>using var scope</c> in the composition-root
    /// DI-validation tests, which never start the host). The production path disposes the root provider
    /// asynchronously, which uses <see cref="DisposeAsync"/> instead. When nothing was started this is a
    /// no-op; if a server is running it is stopped synchronously as a last resort.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_started && _options.Embedded && _provisioner is not null)
        {
            // Fallback sync teardown; the async DisposeAsync path is preferred in production.
            _provisioner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        _started = false;
        _connectionString = null;
        _disposed = true;
    }
}
