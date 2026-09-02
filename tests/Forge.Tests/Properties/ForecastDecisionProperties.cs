using System.Globalization;
using CsCheck;
using Forge.Application.Abstractions;
using Forge.Application.Forecasting;
using Forge.Domain.Common;
using Xunit;

namespace Forge.Tests.Properties;

/// <summary>
/// Property-based tests for <see cref="SubmitForecastDecisionHandler"/> (task 24.6). They exercise the
/// override-validation boundary across many inputs and the deadline auto-accept predicate across many
/// production/now offsets, complementing the example-based unit tests.
/// Validates: Requirements 22.3, 22.4, 22.5, 22.6.
/// </summary>
public sealed class ForecastDecisionProperties
{
    private static readonly DateTimeOffset Epoch = new(2350, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ColonyId Colony = ColonyId.New();
    private static readonly GelTypeId GelType = GelTypeId.New();

    private const long Min = SubmitForecastDecisionHandler.MinOverride;         // 0
    private const long Max = SubmitForecastDecisionHandler.MaxOverride;         // 999,999,999

    // A whole number rendered as its invariant decimal string, spanning below/at/within/above the
    // valid override range so both accept and reject paths are reached frequently.
    private static readonly Gen<long> GenNumber = Gen.Long[-10L, Max + 1_000L];

    // Non-numeric / empty inputs that must always be rejected (Req 22.4). Rendered by picking an index
    // to sidestep the Gen.Const overload ambiguity when the value is a bare null literal.
    private static readonly string?[] NonNumericValues =
    {
        null,       // empty (absent)
        "",         // empty
        "   ",      // whitespace
        "abc",      // non-numeric
        "12.5",     // non-integer
        "1e3",      // scientific -> non-integer form
        "1,000",    // grouping separator -> not a plain integer
    };

    private static readonly Gen<string?> GenNonNumeric =
        Gen.Int[0, NonNumericValues.Length - 1].Select(i => NonNumericValues[i]);

    /// <summary>
    /// A numeric override is accepted iff it parses to a whole number within <c>0..999,999,999</c>.
    /// When accepted the forecast value is replaced, the state settles Overridden, and an audit is
    /// recorded carrying the original value, override value, operator id, and timestamp (Req 22.3, 22.5).
    /// When out of range it is rejected and the original is retained with no audit (Req 22.4).
    /// <para>**Validates: Requirements 22.3, 22.4, 22.5**</para>
    /// </summary>
    [Fact]
    public void NumericOverride_isAcceptedIffInRange_elseRejectedAndRetained()
    {
        Gen.Select(GenNumber, Gen.Double[-1_000.0, 1_000.0])
            .Sample((number, originalDemand) =>
            {
                var raw = number.ToString(CultureInfo.InvariantCulture);
                var sink = new RecordingAuditSink();
                var handler = new SubmitForecastDecisionHandler(new FixedClock(Epoch), sink);
                var pending = Pending(originalDemand);

                var result = handler
                    .HandleAsync(pending, SubmitForecastDecisionCommand.Override("op-x", raw))
                    .GetAwaiter().GetResult();

                var inRange = number >= Min && number <= Max;

                if (inRange)
                {
                    Assert.True(result.IsSuccess);
                    Assert.Equal(ForecastState.Overridden, result.Value.State);
                    Assert.Equal((double)number, result.Value.Lifecycle.Forecast.ExpectedDemand);

                    var audit = Assert.Single(sink.Recorded);
                    Assert.Equal(originalDemand, audit.OriginalValue);
                    Assert.Equal(number, audit.OverrideValue);
                    Assert.Equal("op-x", audit.OperatorId);
                    Assert.Equal(Epoch, audit.Timestamp);
                }
                else
                {
                    Assert.True(result.IsFailure);
                    Assert.Equal(ErrorKind.Validation, result.Error.Kind);
                    // Retained: caller-held lifecycle unchanged, no audit (Req 22.4).
                    Assert.Equal(ForecastState.Pending, pending.State);
                    Assert.Equal(originalDemand, pending.Forecast.ExpectedDemand);
                    Assert.Empty(sink.Recorded);
                }
            });
    }

    /// <summary>
    /// A non-numeric or empty override is always rejected, retaining the original forecast and recording
    /// no audit (Req 22.4).
    /// <para>**Validates: Requirements 22.4**</para>
    /// </summary>
    [Fact]
    public void NonNumericOrEmptyOverride_isAlwaysRejectedAndRetained()
    {
        Gen.Select(GenNonNumeric, Gen.Double[-1_000.0, 1_000.0])
            .Sample((raw, originalDemand) =>
            {
                var sink = new RecordingAuditSink();
                var handler = new SubmitForecastDecisionHandler(new FixedClock(Epoch), sink);
                var pending = Pending(originalDemand);

                var result = handler
                    .HandleAsync(pending, SubmitForecastDecisionCommand.Override("op", raw))
                    .GetAwaiter().GetResult();

                Assert.True(result.IsFailure);
                Assert.Equal(ErrorKind.Validation, result.Error.Kind);
                Assert.Equal(ForecastState.Pending, pending.State);
                Assert.Equal(originalDemand, pending.Forecast.ExpectedDemand);
                Assert.Empty(sink.Recorded);
            });
    }

    /// <summary>
    /// Auto-accept settles a pending forecast as Accepted_By_Default iff at least the configured deadline
    /// has elapsed since production (<c>now - producedAt &gt;= deadline</c>); otherwise it stays Pending.
    /// The produced values are preserved on auto-accept, and no audit is recorded (Req 22.6).
    /// <para>**Validates: Requirements 22.6**</para>
    /// </summary>
    [Fact]
    public void AutoAccept_settlesByDefaultIffDeadlineElapsed()
    {
        // Deadline in [1, 168] hours; elapsed offset spans below and above it.
        Gen.Select(Gen.Int[1, 168], Gen.Int[0, 200], Gen.Double[-1_000.0, 1_000.0])
            .Sample((deadlineHours, elapsedHours, demand) =>
            {
                var deadline = ForecastDecisionDeadline.Create(TimeSpan.FromHours(deadlineHours)).Value;
                var now = Epoch + TimeSpan.FromHours(elapsedHours);
                var sink = new RecordingAuditSink();
                var handler = new SubmitForecastDecisionHandler(new FixedClock(now), sink, deadline);
                var pending = Pending(demand);

                var outcome = handler.AutoAcceptIfElapsed(pending, Epoch);

                var elapsed = elapsedHours >= deadlineHours;
                if (elapsed)
                {
                    Assert.Equal(ForecastState.Accepted_By_Default, outcome.State);
                    Assert.Equal(demand, outcome.Lifecycle.Forecast.ExpectedDemand);
                }
                else
                {
                    Assert.Equal(ForecastState.Pending, outcome.State);
                }

                Assert.Null(outcome.Audit);
                Assert.Empty(sink.Recorded);
            });
    }

    private static ForecastLifecycle Pending(double demand) =>
        ForecastLifecycle.Pending(new DemandForecast(
            Colony, GelType, TimeSpan.FromDays(7), demand, IsFallback: false));

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
