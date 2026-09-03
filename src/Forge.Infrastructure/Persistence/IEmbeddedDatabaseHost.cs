namespace Forge.Infrastructure.Persistence;

/// <summary>
/// A bootstrap seam for the persistence backing store (Req 26.1). The design targets the Npgsql EF
/// Core provider (<c>Npgsql.EntityFrameworkCore.PostgreSQL</c>) for <b>both</b> the default embedded
/// Postgres and a container-hosted Postgres, distinguishing the two <b>by connection string only</b>
/// (design "Persistence and Seeding"). This abstraction is that "server bootstrap": it starts (or
/// attaches to) the database, exposes the connection string the <see cref="ForgeDbContext"/> then
/// binds to, and stops it on shutdown.
/// <para>
/// The embedded implementation (<see cref="EmbeddedDatabaseHost"/>) manages a local Postgres instance
/// in default mode; a container-hosted deployment supplies an externally-managed connection string and
/// performs no lifecycle management. Because both paths yield an identical Npgsql connection string,
/// the EF Core provider code is identical between embedded and Docker-Compose Postgres — swapping one
/// for the other never changes the DbContext or the Application layer.
/// </para>
/// <para>
/// This is intentionally a lifecycle/bootstrap seam only. Applying EF Core migrations and running the
/// seeder on an empty database (Req 26.2, 26.3, 26.5) are the responsibility of the Api startup path
/// (task 28.2), which calls <see cref="StartAsync"/> first and fails startup with a descriptive error
/// if the store cannot be initialized (Req 26.5).
/// </para>
/// </summary>
public interface IEmbeddedDatabaseHost : IAsyncDisposable
{
    /// <summary>
    /// The Npgsql connection string the <see cref="ForgeDbContext"/> binds to once the host has
    /// started. Valid only after <see cref="StartAsync"/> has completed successfully; accessing it
    /// before start throws <see cref="InvalidOperationException"/>.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Whether this host manages an embedded (locally-provisioned) Postgres instance
    /// (<c>true</c>) or merely attaches to an externally-provided connection string such as a
    /// container-hosted Postgres (<c>false</c>). This is derived from configuration/connection
    /// string only and does not change the EF Core provider path (design "Persistence and Seeding").
    /// </summary>
    bool IsEmbedded { get; }

    /// <summary>
    /// Start (embedded) or attach to (container) the backing Postgres so that
    /// <see cref="ConnectionString"/> becomes valid (Req 26.1). Idempotent: a second call after a
    /// successful start is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancels a slow startup.</param>
    /// <exception cref="EmbeddedDatabaseHostException">
    /// The database cannot be initialized; the Api startup path surfaces this as a descriptive
    /// startup failure (Req 26.5).
    /// </exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the embedded Postgres instance if this host manages one; a no-op for a container-hosted
    /// connection. Idempotent and safe to call whether or not <see cref="StartAsync"/> ran.
    /// </summary>
    /// <param name="cancellationToken">Cancels a slow shutdown.</param>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raised when the backing Postgres database cannot be initialized (Req 26.5). The Api startup path
/// catches this and fails startup with a descriptive error naming the underlying cause.
/// </summary>
public sealed class EmbeddedDatabaseHostException : Exception
{
    /// <summary>Create the exception with a descriptive message (Req 26.5).</summary>
    public EmbeddedDatabaseHostException(string message)
        : base(message)
    {
    }

    /// <summary>Create the exception with a descriptive message and the underlying cause (Req 26.5).</summary>
    public EmbeddedDatabaseHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
