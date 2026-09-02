using Forge.Application.Abstractions;
using Forge.Application.Forecasting;
using Forge.Domain.Common;
using Xunit;

namespace Forge.Tests.Forecasting;

/// <summary>
/// Unit tests for the Application <see cref="SubmitForecastDecisionHandler"/> (task 24.6): accept applies
/// the produced values and settles Accepted; a validated override in <c>0..999,999,999</c> replaces the
/// values, settles Overridden, and records an audit (original, override, operator id, timestamp); an
/// invalid / non-numeric / empty override rejects and retains the original with no audit; and the
/// configured deadline auto-accepts a pending forecast as Accepted_By_Default.
/// Validates: Requirements 9.4, 22.2, 22.3, 22.4, 22.5, 22.6.
/// </summary>
public sealed class SubmitForecastDecisionHandlerTests
{
    private static readonly DateTimeOffset Produced = new(2350, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ColonyId Colony = ColonyId.New();
    private static readonly GelTypeId GelType = GelTypeId.New();

    // ---- Accept (Req 22.2) ----

    [Fact]
    public async Task Accept_settles_Accepted_keeps_values_and_records_no_audit()
    {
        var (handler, sink) = Build(Produced);
        var pending = PendingForecast(expectedDemand: 42.0);

        var result = await handler.HandleAsync(pending, SubmitForecastDecisionCommand.Accept("op-1"));

        Assert.True(result.IsSuccess);
        Assert.Equal(ForecastState.Accepted, result.Value.State);
        Assert.Equal(42.0, result.Value.Lifecycle.Forecast.ExpectedDemand);
        Assert.Null(result.Value.Audit);
        Assert.Empty(sink.Recorded);
    }

    // ---- Valid override + audit (Req 22.3, 22.5) ----

    [Fact]
    public async Task Override_valid_value_replaces_values_settles_Overridden_and_records_audit()
    {
        var at = Produced.AddHours(2);
        var (handler, sink) = Build(at);
        var pending = PendingForecast(expectedDemand: 42.0);

        var result = await handler.HandleAsync(pending, SubmitForecastDecisionCommand.Override("op-7", "1000"));

        Assert.True(result.IsSuccess);
        Assert.Equal(ForecastState.Overridden, result.Value.State);
        Assert.Equal(1000.0, result.Value.Lifecycle.Forecast.ExpectedDemand);

        var audit = Assert.Single(sink.Recorded);
        Assert.Same(audit, result.Value.Audit);
        Assert.Equal(Colony, audit.Colony);
        Assert.Equal(GelType, audit.GelType);
        Assert.Equal(42.0, audit.OriginalValue);
        Assert.Equal(1000L, audit.OverrideValue);
        Assert.Equal("op-7", audit.OperatorId);
        Assert.Equal(at, audit.Timestamp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999999999")]
    public async Task Override_accepts_the_inclusive_range_bounds(string raw)
    {
        var (handler, sink) = Build(Produced);
        var pending = PendingForecast(expectedDemand: 5.0);

        var result = await handler.HandleAsync(pending, SubmitForecastDecisionCommand.Override("op", raw));

        Assert.True(result.IsSuccess);
        Assert.Equal(ForecastState.Overridden, result.Value.State);
        Assert.Single(sink.Recorded);
    }

    // ---- Invalid override rejection with retention (Req 22.4) ----

    [Theory]
    [InlineData(null)]          // empty (absent)
    [InlineData("")]            // empty
    [InlineData("   ")]         // whitespace
    [InlineData("abc")]         // non-numeric
    [InlineData("12.5")]        // non-integer
    [InlineData("-1")]          // below range
    [InlineData("1000000000")]  // above range (1e9 > 999,999,999)
    [InlineData("1e3")]         // non-integer scientific form
    public async Task Override_invalid_value_is_rejected_and_original_retained_with_no_audit(string? raw)
    {
        var (handler, sink) = Build(Produced);
        var pending = PendingForecast(expectedDemand: 42.0);

        var result = await handler.HandleAsync(pending, SubmitForecastDecisionCommand.Override("op", raw));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);

        // The caller-held lifecycle is untouched: still Pending with the original value (Req 22.4).
        Assert.Equal(ForecastState.Pending, pending.State);
        Assert.Equal(42.0, pending.Forecast.ExpectedDemand);
        Assert.Empty(sink.Recorded);
    }

    // ---- Deadline auto-accept (Req 22.6) ----

    [Fact]
    public void AutoAccept_before_deadline_leaves_forecast_pending()
    {
        // 24h default deadline; only 23h have elapsed.
        var (handler, _) = Build(Produced.AddHours(23));
        var pending = PendingForecast(expectedDemand: 7.0);

        var outcome = handler.AutoAcceptIfElapsed(pending, Produced);

        Assert.Equal(ForecastState.Pending, outcome.State);
        Assert.Null(outcome.Audit);
    }

    [Fact]
    public void AutoAccept_at_or_after_deadline_settles_Accepted_By_Default_keeping_values()
    {
        // 24h default deadline; exactly 24h have elapsed.
        var (handler, sink) = Build(Produced.AddHours(24));
        var pending = PendingForecast(expectedDemand: 7.0);

        var outcome = handler.AutoAcceptIfElapsed(pending, Produced);

        Assert.Equal(ForecastState.Accepted_By_Default, outcome.State);
        Assert.Equal(7.0, outcome.Lifecycle.Forecast.ExpectedDemand);
        Assert.Null(outcome.Audit);           // auto-accept is not an override, so no audit
        Assert.Empty(sink.Recorded);
    }

    [Fact]
    public void AutoAccept_honors_a_configured_non_default_deadline()
    {
        var deadline = ForecastDecisionDeadline.Create(TimeSpan.FromHours(1)).Value;
        var sink = new RecordingAuditSink();
        var handler = new SubmitForecastDecisionHandler(new FixedClock(Produced.AddHours(1)), sink, deadline);
        var pending = PendingForecast(expectedDemand: 3.0);

        var outcome = handler.AutoAcceptIfElapsed(pending, Produced);

        Assert.Equal(ForecastState.Accepted_By_Default, outcome.State);
    }

    // ---- Guard: a settled forecast cannot be decided again ----

    [Fact]
    public async Task Decision_on_already_settled_forecast_is_rejected()
    {
        var (handler, sink) = Build(Produced);
        var accepted = new ForecastLifecycle(
            ForecastingService.FallbackForecast(Colony, GelType, TimeSpan.FromDays(7)),
            ForecastState.Accepted);

        var result = await handler.HandleAsync(accepted, SubmitForecastDecisionCommand.Accept("op"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Empty(sink.Recorded);
    }

    [Fact]
    public void Deadline_outside_valid_range_is_rejected()
    {
        Assert.True(ForecastDecisionDeadline.Create(TimeSpan.FromMinutes(59)).IsFailure);
        Assert.True(ForecastDecisionDeadline.Create(TimeSpan.FromHours(169)).IsFailure);
        Assert.True(ForecastDecisionDeadline.Create(TimeSpan.FromHours(1)).IsSuccess);
        Assert.True(ForecastDecisionDeadline.Create(TimeSpan.FromHours(168)).IsSuccess);
        Assert.Equal(TimeSpan.FromHours(24), ForecastDecisionDeadline.Default.Duration);
    }

    // ---- Harness ----

    private static (SubmitForecastDecisionHandler Handler, RecordingAuditSink Sink) Build(DateTimeOffset now)
    {
        var sink = new RecordingAuditSink();
        var handler = new SubmitForecastDecisionHandler(new FixedClock(now), sink);
        return (handler, sink);
    }

    private static ForecastLifecycle PendingForecast(double expectedDemand) =>
        ForecastLifecycle.Pending(new DemandForecast(
            Colony, GelType, TimeSpan.FromDays(7), expectedDemand, IsFallback: false));

    private sealed class RecordingAuditSink : IForecastAuditSink
    {
        public List<PredictionOverrideAudit> Recorded { get; } = new();

        public Task RecordOverrideAsync(PredictionOverrideAudit audit, CancellationToken ct = default)
        {
            Recorded.Add(audit);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => Now = now;

        public DateTimeOffset Now { get; }
        public ClockMode Mode => ClockMode.Paused;
        public double AccelerationFactor => 1;
        public void Configure(ClockMode mode, double accelerationFactor) { }
        public void Pause() { }
        public void Resume() { }
        public TimeSpan Advance(TimeSpan wallDelta) => TimeSpan.Zero;
    }
}
