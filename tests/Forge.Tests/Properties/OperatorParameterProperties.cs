using System.Globalization;
using CsCheck;
using Forge.Application.OperatorParameters;
using Forge.Contracts.OperatorParameters;
using Forge.Domain.Common;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 14: Operator-parameter validation
/// <summary>
/// Property 14 (design.md): <em>For any</em> operator parameter and any submitted value, an in-range
/// value of the correct type SHALL be accepted and applied (the live state reflects the new value),
/// and an out-of-range or wrong-type value SHALL be rejected with the previous value retained and the
/// invalid parameter identified.
/// <para>
/// Each test seeds an <see cref="OperatorParameterState"/> with a random configured/starting value,
/// submits a random value (drawn from a mix of valid and invalid/wrong-type generators) through
/// <see cref="OperatorParameterService.Apply"/>, and asserts the accept/apply and reject/retain
/// contract of Req 20.8. Determinism holds because the service depends only on the injected state and
/// the submitted value — no clock or driver is wired in these tests.
/// </para>
/// <para><b>Validates: Requirements 20.8, 28.4.</b></para>
/// </summary>
public sealed class OperatorParameterProperties
{
    private const int Iterations = 100;

    // Configured upper bounds for the two deployment-dependent parameters.
    private static readonly Gen<int> GenWorkerMax = Gen.Int[0, 50];
    private static readonly Gen<int> GenModeledDockBays = Gen.Int[0, 30];

    // ---- Value generators: valid vs invalid/wrong-type, rendered as the submitted string ----

    // Non-negative finite doubles (valid for sim-speed / inbound-rate / demand-multiplier).
    private static readonly Gen<double> GenValidNonNegativeDouble =
        Gen.Double[0.0, 1_000.0];

    // Doubles that are out of range (negative) OR special (NaN / infinity) — all invalid.
    private static readonly Gen<string> GenInvalidDoubleString =
        Gen.OneOf(
            Gen.Double[-1_000.0, -0.0001].Select(RenderDouble),
            Gen.Const(double.NaN).Select(RenderDouble),
            Gen.Const(double.PositiveInfinity).Select(RenderDouble),
            Gen.Const(double.NegativeInfinity).Select(RenderDouble));

    // Wrong-type strings for a numeric parameter (never parse as a number).
    private static readonly Gen<string> GenWrongTypeString =
        Gen.OneOf(
            Gen.Const(""),
            Gen.Const("   "),
            Gen.Const("abc"),
            Gen.Const("true"),
            Gen.Const("1.2.3"),
            Gen.Const("12px"),
            Gen.Const("NaNa"));

    private static string RenderDouble(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    private static string RenderInt(int i) => i.ToString(CultureInfo.InvariantCulture);

    private static OperatorParameterState NewState(int workerMax, int modeledDockBays) =>
        new(new OperatorParameterOptions { WorkerMax = workerMax, ModeledDockBays = modeledDockBays });

    /// <summary>
    /// Assert the accept/apply outcome: the change succeeded, the target property now equals the
    /// submitted value, and no other property drifted from its starting value.
    /// </summary>
    private static void AssertAccepted(
        Result result,
        OperatorParameterState state,
        OperatorParameterStateSnapshot before,
        Func<OperatorParameterState, bool> targetMatches,
        Func<OperatorParameterStateSnapshot, OperatorParameterStateSnapshot> expectedAfter)
    {
        Assert.True(result.IsSuccess, $"Expected accept but was rejected: {(result.IsFailure ? result.Error.Message : "")}");
        Assert.True(targetMatches(state), "Accepted change was not reflected in the live state.");

        var expected = expectedAfter(before);
        Assert.Equal(expected, OperatorParameterStateSnapshot.From(state));
    }

    /// <summary>
    /// Assert the reject/retain outcome: the change failed with a validation error naming the
    /// parameter, and every property is unchanged from its starting value (Req 20.8).
    /// </summary>
    private static void AssertRejected(
        Result result,
        OperatorParameterState state,
        OperatorParameterStateSnapshot before,
        string parameterKey)
    {
        Assert.True(result.IsFailure, "Expected reject but the change was accepted.");
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);

        Assert.NotNull(result.Error.Detail);
        Assert.True(result.Error.Detail!.TryGetValue("parameter", out var named));
        Assert.Equal(parameterKey, named);

        // Previous value retained: nothing changed anywhere.
        Assert.Equal(before, OperatorParameterStateSnapshot.From(state));
    }

    [Fact]
    public void SimSpeed_ValidIsAppliedInvalidIsRejectedAndRetained()
    {
        Gen.Select(
                GenWorkerMax,
                GenModeledDockBays,
                Gen.Bool,
                GenValidNonNegativeDouble,
                Gen.OneOf(GenInvalidDoubleString, GenWrongTypeString))
            .Sample((workerMax, dockBays, useValid, validValue, invalidValue) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);

                if (useValid)
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.SimSpeed, RenderDouble(validValue)));

                    AssertAccepted(result, state, before,
                        s => s.SimSpeed == validValue,
                        b => b with { SimSpeed = validValue });
                }
                else
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.SimSpeed, invalidValue));

                    AssertRejected(result, state, before, OperatorParameterKey.SimSpeed);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void InboundRate_ValidIsAppliedInvalidIsRejectedAndRetained()
    {
        Gen.Select(
                GenWorkerMax,
                GenModeledDockBays,
                Gen.Bool,
                GenValidNonNegativeDouble,
                Gen.OneOf(GenInvalidDoubleString, GenWrongTypeString))
            .Sample((workerMax, dockBays, useValid, validValue, invalidValue) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);

                if (useValid)
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.InboundRate, RenderDouble(validValue)));

                    AssertAccepted(result, state, before,
                        s => s.InboundRate == validValue,
                        b => b with { InboundRate = validValue });
                }
                else
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.InboundRate, invalidValue));

                    AssertRejected(result, state, before, OperatorParameterKey.InboundRate);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void DemandMultiplier_ValidIsAppliedInvalidIsRejectedAndRetained()
    {
        Gen.Select(
                GenWorkerMax,
                GenModeledDockBays,
                Gen.Bool,
                GenValidNonNegativeDouble,
                Gen.OneOf(GenInvalidDoubleString, GenWrongTypeString))
            .Sample((workerMax, dockBays, useValid, validValue, invalidValue) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);

                if (useValid)
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.DemandMultiplier, RenderDouble(validValue)));

                    AssertAccepted(result, state, before,
                        s => s.DemandMultiplier == validValue,
                        b => b with { DemandMultiplier = validValue });
                }
                else
                {
                    var result = new OperatorParameterService(state)
                        .Apply(new OperatorParameterDto(OperatorParameterKey.DemandMultiplier, invalidValue));

                    AssertRejected(result, state, before, OperatorParameterKey.DemandMultiplier);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void WorkersOnShift_ValidIsAppliedOutOfRangeAndWrongTypeAreRejectedAndRetained()
    {
        Gen.Select(
                GenWorkerMax,
                GenModeledDockBays,
                Gen.Int[-20, 80],
                GenWrongTypeString,
                Gen.Bool)
            .Sample((workerMax, dockBays, submitted, wrongType, useWrongType) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);
                var service = new OperatorParameterService(state);

                if (useWrongType)
                {
                    var result = service.Apply(new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, wrongType));
                    AssertRejected(result, state, before, OperatorParameterKey.WorkersOnShift);
                    return;
                }

                var numeric = service.Apply(
                    new OperatorParameterDto(OperatorParameterKey.WorkersOnShift, RenderInt(submitted)));

                var inRange = submitted >= OperatorParameterRanges.WorkersOnShiftMin && submitted <= workerMax;
                if (inRange)
                {
                    AssertAccepted(numeric, state, before,
                        s => s.WorkersOnShift == submitted,
                        b => b with { WorkersOnShift = submitted });
                }
                else
                {
                    AssertRejected(numeric, state, before, OperatorParameterKey.WorkersOnShift);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void OpenDockBays_ValidIsAppliedOutOfRangeAndWrongTypeAreRejectedAndRetained()
    {
        Gen.Select(
                GenWorkerMax,
                GenModeledDockBays,
                Gen.Int[-20, 60],
                GenWrongTypeString,
                Gen.Bool)
            .Sample((workerMax, dockBays, submitted, wrongType, useWrongType) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);
                var service = new OperatorParameterService(state);

                if (useWrongType)
                {
                    var result = service.Apply(new OperatorParameterDto(OperatorParameterKey.OpenDockBays, wrongType));
                    AssertRejected(result, state, before, OperatorParameterKey.OpenDockBays);
                    return;
                }

                var numeric = service.Apply(
                    new OperatorParameterDto(OperatorParameterKey.OpenDockBays, RenderInt(submitted)));

                var inRange = submitted >= OperatorParameterRanges.OpenDockBaysMin && submitted <= dockBays;
                if (inRange)
                {
                    AssertAccepted(numeric, state, before,
                        s => s.OpenDockBays == submitted,
                        b => b with { OpenDockBays = submitted });
                }
                else
                {
                    AssertRejected(numeric, state, before, OperatorParameterKey.OpenDockBays);
                }
            }, iter: Iterations);
    }

    [Fact]
    public void SlottingStrategy_KnownKeyIsAppliedUnknownIsRejectedAndRetained()
    {
        // Mix known keys (valid) with arbitrary/unknown strings (invalid) so both branches are hit.
        var genKey = Gen.OneOf(
            Gen.OneOfConst(SlottingStrategyKey.VelocityAffinity, SlottingStrategyKey.NaiveFirstAvailable),
            Gen.OneOfConst("", "  ", "unknown-strategy", "velocity", "fifo", "VELOCITY-AFFINITY"));

        Gen.Select(GenWorkerMax, GenModeledDockBays, genKey)
            .Sample((workerMax, dockBays, key) =>
            {
                var state = NewState(workerMax, dockBays);
                var before = OperatorParameterStateSnapshot.From(state);

                var result = new OperatorParameterService(state)
                    .Apply(new OperatorParameterDto(OperatorParameterKey.SlottingStrategy, key));

                if (SlottingStrategyKey.IsValid(key))
                {
                    AssertAccepted(result, state, before,
                        s => s.SlottingStrategy == key,
                        b => b with { SlottingStrategy = key });
                }
                else
                {
                    AssertRejected(result, state, before, OperatorParameterKey.SlottingStrategy);
                }
            }, iter: Iterations);
    }

    /// <summary>An immutable snapshot of the six live parameter values, used to assert retain/apply.</summary>
    private readonly record struct OperatorParameterStateSnapshot(
        double SimSpeed,
        int WorkersOnShift,
        int OpenDockBays,
        double InboundRate,
        double DemandMultiplier,
        string SlottingStrategy)
    {
        public static OperatorParameterStateSnapshot From(OperatorParameterState state) => new(
            state.SimSpeed,
            state.WorkersOnShift,
            state.OpenDockBays,
            state.InboundRate,
            state.DemandMultiplier,
            state.SlottingStrategy);
    }
}
