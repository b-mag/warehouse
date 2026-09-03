using System.Collections.Concurrent;
using Forge.Application.Abstractions;
using Forge.Application.Forecasting;
using Microsoft.Extensions.Logging;

namespace Forge.Infrastructure.Adapters;

/// <summary>
/// The Phase-1 implementation of the Application <see cref="IForecastAuditSink"/> seam (task 33.3;
/// Req 22.5). It records each applied <see cref="PredictionOverrideAudit"/> to the log and retains it
/// in an in-memory, append-only buffer.
/// <para>
/// <b>Why in-memory + logging for Phase 1.</b> There is no override-audit table in the current
/// <c>ForgeDbContext</c> model (task 28.1 mapped the warehouse aggregates — gel types, lots, zones,
/// colonies, orders, starships, workers, dock bays, pick faces, tasks — but no forecast-audit entity),
/// and adding one is out of scope for the composition-root task. Keeping the audit in memory + the log
/// satisfies Req 22.5's "record the audit" on the override success path while leaving a single, obvious
/// seam for a durable EF-backed sink when a forecast-audit table lands: swap this registration, no
/// change to the override handler. The retained list is exposed for tests/diagnostics.
/// </para>
/// <para>
/// The sink is invoked only after an override validates (Req 22.4), so it never persists an audit for a
/// rejected override. It is registered as a singleton so the retained audits survive across scoped
/// operations; the concurrent buffer makes concurrent overrides safe.
/// </para>
/// </summary>
public sealed class LoggingForecastAuditSink : IForecastAuditSink
{
    private readonly ILogger<LoggingForecastAuditSink> _logger;
    private readonly ConcurrentQueue<PredictionOverrideAudit> _audits = new();

    /// <summary>Create the sink over the logger it writes each recorded override to.</summary>
    public LoggingForecastAuditSink(ILogger<LoggingForecastAuditSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The overrides recorded so far, in the order they were applied (diagnostics/tests).</summary>
    public IReadOnlyCollection<PredictionOverrideAudit> RecordedAudits => _audits.ToArray();

    /// <inheritdoc />
    public Task RecordOverrideAsync(PredictionOverrideAudit audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);

        _audits.Enqueue(audit);
        _logger.LogInformation(
            "Forecast override recorded: colony {Colony} gelType {GelType} original {Original} -> override {Override} by {Operator} at {Timestamp:o}.",
            audit.Colony,
            audit.GelType,
            audit.OriginalValue,
            audit.OverrideValue,
            audit.OperatorId,
            audit.Timestamp);

        return Task.CompletedTask;
    }
}
