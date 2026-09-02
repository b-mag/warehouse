using System.Globalization;
using Forge.Application.Abstractions;
using Forge.Domain.Common;

namespace Forge.Application.Forecasting;

/// <summary>
/// The human-in-the-loop decision handler for produced demand forecasts (task 24.6; Req 22.2–22.6;
/// design "ML Forecasting and Human-in-the-Loop"). It moves a <see cref="ForecastState.Pending"/>
/// forecast to <see cref="ForecastState.Accepted"/>, <see cref="ForecastState.Overridden"/>, or
/// <see cref="ForecastState.Accepted_By_Default"/>.
/// <para>
/// <b>Accept (Req 22.2).</b> <see cref="HandleAsync"/> with <see cref="ForecastDecisionKind.Accept"/>
/// keeps the forecast's produced values and moves it to <see cref="ForecastState.Accepted"/>. Those
/// values are the ones that apply downstream.
/// </para>
/// <para>
/// <b>Override (Req 22.3, 22.4, 22.5).</b> With <see cref="ForecastDecisionKind.Override"/> the raw
/// operator input is validated against the inclusive range <c>0..999,999,999</c>. An empty,
/// non-numeric, non-integer, or out-of-range value is rejected with
/// <see cref="DomainError.Validation(string, string?)"/>; the original forecast is retained and no
/// audit is recorded (Req 22.4). A valid value replaces the forecast's expected demand, moves it to
/// <see cref="ForecastState.Overridden"/>, and records a <see cref="PredictionOverrideAudit"/> through
/// <see cref="IForecastAuditSink"/> capturing the original value, override value, operator id, and the
/// timestamp from <see cref="IClock.Now"/> (Req 22.5).
/// </para>
/// <para>
/// <b>Auto-accept on deadline (Req 22.6).</b> <see cref="AutoAcceptIfElapsedAsync"/> applies the
/// forecast as the default and moves it to <see cref="ForecastState.Accepted_By_Default"/> once the
/// configured deadline (<see cref="ForecastDecisionDeadline"/>, <c>1..168</c> hours, default 24h) has
/// elapsed since the forecast was produced, evaluated against <see cref="IClock.Now"/>. Before the
/// deadline it makes no change and returns the still-pending lifecycle.
/// </para>
/// <para>
/// <b>Layering &amp; determinism.</b> The handler depends only on Application abstractions
/// (<see cref="IClock"/>, <see cref="IForecastAuditSink"/>) and pure Application/Domain types, keeping
/// the WMS Core's Domain+Contracts-only boundary intact. It is a pure function of its inputs plus the
/// clock: an already-decided forecast is only transitioned from <see cref="ForecastState.Pending"/>, so
/// a second decision on a settled forecast is rejected, leaving it unchanged.
/// </para>
/// </summary>
public sealed class SubmitForecastDecisionHandler
{
    /// <summary>The inclusive lower bound for a valid override value (Req 22.3, 22.4).</summary>
    public const long MinOverride = 0L;

    /// <summary>The inclusive upper bound for a valid override value (Req 22.3, 22.4).</summary>
    public const long MaxOverride = 999_999_999L;

    private readonly IClock _clock;
    private readonly IForecastAuditSink _auditSink;
    private readonly ForecastDecisionDeadline _deadline;

    /// <summary>
    /// Construct the handler over its Application seams. The optional <paramref name="deadline"/>
    /// configures the auto-accept window; when omitted the default 24-hour deadline is used (Req 22.6).
    /// </summary>
    /// <param name="clock">The clock supplying the override/auto-accept timestamp and "now" (Req 22.5, 22.6).</param>
    /// <param name="auditSink">The sink an override audit is recorded through (Req 22.5).</param>
    /// <param name="deadline">The configured auto-accept deadline; defaults to 24 hours (Req 22.6).</param>
    public SubmitForecastDecisionHandler(
        IClock clock,
        IForecastAuditSink auditSink,
        ForecastDecisionDeadline? deadline = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _deadline = deadline ?? ForecastDecisionDeadline.Default;
    }

    /// <summary>The auto-accept deadline this handler enforces (Req 22.6).</summary>
    public ForecastDecisionDeadline Deadline => _deadline;

    /// <summary>
    /// Apply an operator decision to a pending <paramref name="lifecycle"/> (Req 22.2–22.5). Returns the
    /// transitioned <see cref="ForecastDecisionOutcome"/> on success, or a typed rejection that leaves
    /// the forecast unchanged (an invalid/non-numeric/empty override, or a decision on an
    /// already-settled forecast) (Req 22.4).
    /// </summary>
    /// <param name="lifecycle">The pending forecast to decide on.</param>
    /// <param name="command">The operator's accept/override decision.</param>
    /// <param name="ct">A cancellation token for the async audit write.</param>
    public async Task<Result<ForecastDecisionOutcome>> HandleAsync(
        ForecastLifecycle lifecycle,
        SubmitForecastDecisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(command);

        // A decision only applies to a forecast still awaiting one; a settled forecast is left as-is.
        if (lifecycle.State != ForecastState.Pending)
        {
            return DomainError.Validation(
                $"Forecast is already in state '{lifecycle.State.ToWireName()}' and cannot be decided again.",
                nameof(lifecycle));
        }

        return command.Kind switch
        {
            ForecastDecisionKind.Accept => Accept(lifecycle),
            ForecastDecisionKind.Override => await OverrideAsync(lifecycle, command, ct).ConfigureAwait(false),
            _ => DomainError.Validation(
                $"Unknown forecast decision kind '{command.Kind}'.", nameof(command.Kind)),
        };
    }

    /// <summary>
    /// Auto-accept a still-<see cref="ForecastState.Pending"/> forecast as the default once the
    /// configured deadline has elapsed since <paramref name="producedAt"/>, evaluated against
    /// <see cref="IClock.Now"/> (Req 22.6). If the deadline has not elapsed, or the forecast is already
    /// settled, the lifecycle is returned unchanged.
    /// </summary>
    /// <param name="lifecycle">The forecast to evaluate.</param>
    /// <param name="producedAt">The time the forecast was produced (deadline is measured from here).</param>
    /// <returns>
    /// A <see cref="ForecastState.Accepted_By_Default"/> outcome when the deadline elapsed; otherwise an
    /// outcome carrying the unchanged lifecycle.
    /// </returns>
    public ForecastDecisionOutcome AutoAcceptIfElapsed(
        ForecastLifecycle lifecycle,
        DateTimeOffset producedAt)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);

        if (lifecycle.State != ForecastState.Pending || !_deadline.HasElapsed(producedAt, _clock.Now))
        {
            return new ForecastDecisionOutcome(lifecycle, Audit: null);
        }

        // Apply the produced values as the default (Req 22.6). No audit — this is not an override.
        var settled = lifecycle with { State = ForecastState.Accepted_By_Default };
        return new ForecastDecisionOutcome(settled, Audit: null);
    }

    private static Result<ForecastDecisionOutcome> Accept(ForecastLifecycle lifecycle)
    {
        // Accept keeps the produced values; only the state advances (Req 22.2).
        var accepted = lifecycle with { State = ForecastState.Accepted };
        return new ForecastDecisionOutcome(accepted, Audit: null);
    }

    private async Task<Result<ForecastDecisionOutcome>> OverrideAsync(
        ForecastLifecycle lifecycle,
        SubmitForecastDecisionCommand command,
        CancellationToken ct)
    {
        // Validate the raw operator input: empty / non-numeric / non-integer / out-of-range all reject,
        // retaining the original forecast unchanged and recording no audit (Req 22.4).
        if (!TryParseOverride(command.OverrideValue, out var overrideValue))
        {
            return DomainError.Validation(
                $"Prediction override '{command.OverrideValue ?? "<empty>"}' is invalid: it must be a " +
                $"whole number in the range {MinOverride}..{MaxOverride}.",
                nameof(command.OverrideValue));
        }

        var original = lifecycle.Forecast;

        // Replace the forecasted values with the operator-supplied value and settle as Overridden
        // (Req 22.3). IsFallback is carried through unchanged — an operator override is not a fallback.
        var overriddenForecast = original with { ExpectedDemand = overrideValue };
        var overridden = new ForecastLifecycle(overriddenForecast, ForecastState.Overridden);

        // Record the audit: original value, override value, operator id, timestamp (Req 22.5).
        var audit = new PredictionOverrideAudit(
            Colony: original.Colony,
            GelType: original.GelType,
            OriginalValue: original.ExpectedDemand,
            OverrideValue: overrideValue,
            OperatorId: command.OperatorId,
            Timestamp: _clock.Now);

        await _auditSink.RecordOverrideAsync(audit, ct).ConfigureAwait(false);

        return new ForecastDecisionOutcome(overridden, audit);
    }

    /// <summary>
    /// Parse and range-check a raw override value. Accepts only a non-empty whole-number string that
    /// parses (invariant culture, integer style) to a value within <c>0..999,999,999</c>; rejects
    /// null, empty/whitespace, non-numeric, non-integer, and out-of-range input (Req 22.3, 22.4).
    /// </summary>
    private static bool TryParseOverride(string? raw, out long value)
    {
        value = 0L;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed < MinOverride || parsed > MaxOverride)
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
