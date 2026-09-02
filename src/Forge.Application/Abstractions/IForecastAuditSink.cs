using Forge.Application.Forecasting;

namespace Forge.Application.Abstractions;

/// <summary>
/// The seam through which the WMS Core records a <see cref="PredictionOverrideAudit"/> when an
/// operator overrides a demand forecast (Req 22.5; design "WMS Core Application abstractions").
/// <para>
/// The Application layer owns this contract; a concrete durable implementation (e.g. an EF Core
/// audit table) lives in <c>Forge.Infrastructure</c>. Keeping it an abstraction preserves the core's
/// Domain+Contracts-only reference boundary and lets the override handler stay unit-testable with an
/// in-memory sink. An override is recorded only after it validates, so the sink is never asked to
/// persist an audit for a rejected override (Req 22.4).
/// </para>
/// </summary>
public interface IForecastAuditSink
{
    /// <summary>
    /// Record the audit for an applied override: the original forecast value, the override value, the
    /// operator identity, and the timestamp (Req 22.5). Invoked only on the override success path.
    /// </summary>
    /// <param name="audit">The audit record to persist.</param>
    /// <param name="ct">A cancellation token for the async persistence operation.</param>
    Task RecordOverrideAsync(PredictionOverrideAudit audit, CancellationToken ct = default);
}
