using CsCheck;

using Forge.Application.Simulation;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 16: Backlog non-negativity and change reflection
//
// Property 16 (design.md): "For any sequence of inbound arrivals and outbound consumption events,
// the exposed receiving and outbound backlog sizes SHALL remain non-negative integers and SHALL
// equal the modeled queue length after each change."
//
// WarehouseMetrics (Application task 22.1) accumulates a receiving backlog as inbound arrivals are
// blocked vs absorbed and an outbound backlog as demand is queued vs served, clamping each at zero
// so a size never goes negative (Req 14.3, 14.4). Every mutation returns a BacklogChanged event with
// the new value when — and only when — the clamped size actually changes (Req 14.7).
//
// This test applies a random sequence of signed change events (positive = arrivals/demand queued,
// negative = absorbed/served) to a WarehouseMetrics and, in parallel, tracks the expected size with
// an independent integer model clamped at zero. After each change it asserts (a) the exposed size is
// non-negative, (b) it equals the independently-modeled clamped queue length, and (c) the returned
// event mirrors the new size on change / is absent when unchanged.
//
// Validates: Requirements 14.3, 14.4, 14.7
public sealed class BacklogProperties
{
    // ≥100 iterations required by the spec.
    private const int Iterations = 100;

    private static readonly DateTimeOffset At =
        new(2400, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // A single change event: which backlog it targets and the signed delta applied to it.
    private sealed record ChangeEvent(BacklogKind Kind, int Delta);

    private static Gen<ChangeEvent> GenChange =>
        from kind in Gen.Int[0, 1].Select(i => i == 0 ? BacklogKind.Receiving : BacklogKind.Outbound)
        // Deltas span negative (absorbed/served) and positive (queued) so the clamp-at-zero path is
        // exercised heavily; the range is wide enough to drive sizes both up and hard down past zero.
        from delta in Gen.Int[-50, 50]
        select new ChangeEvent(kind, delta);

    private static Gen<IReadOnlyList<ChangeEvent>> GenSequence =>
        GenChange.List[0, 100].Select(l => (IReadOnlyList<ChangeEvent>)l);

    // Req 14.3, 14.4, 14.7 / Property 16: after each change the exposed receiving and outbound sizes
    // stay non-negative and equal the independently-modeled clamped queue length.
    [Fact]
    public void BacklogSizes_StayNonNegative_AndMatchModel()
    {
        GenSequence.Sample(sequence =>
        {
            var metrics = new WarehouseMetrics();

            // Independent expected-value model, clamped at zero, mirroring the component's contract.
            int expectedReceiving = 0;
            int expectedOutbound = 0;

            foreach (var change in sequence)
            {
                if (change.Kind == BacklogKind.Receiving)
                {
                    expectedReceiving = Math.Max(0, expectedReceiving + change.Delta);
                }
                else
                {
                    expectedOutbound = Math.Max(0, expectedOutbound + change.Delta);
                }

                metrics.Apply(change.Kind, change.Delta, At);

                // Non-negativity (Req 14.3, 14.4).
                if (metrics.Receiving < 0 || metrics.Outbound < 0)
                {
                    return false;
                }

                // Change reflection: exposed sizes equal the modeled queue lengths (Req 14.7).
                if (metrics.Receiving != expectedReceiving || metrics.Outbound != expectedOutbound)
                {
                    return false;
                }
            }

            return true;
        }, iter: Iterations);
    }

    // Req 14.7 / Property 16: the emitted BacklogChanged event reflects the new size exactly when the
    // clamped size changes, and is absent (null) when the change is a no-op.
    [Fact]
    public void BacklogChangedEvent_ReflectsNewSize_OnlyOnChange()
    {
        GenSequence.Sample(sequence =>
        {
            var metrics = new WarehouseMetrics();

            foreach (var change in sequence)
            {
                int before = metrics.BacklogOf(change.Kind);
                int expectedAfter = Math.Max(0, before + change.Delta);

                var evt = metrics.Apply(change.Kind, change.Delta, At);

                if (expectedAfter == before)
                {
                    // No net change after clamping -> no event (Req 14.7).
                    if (evt is not null)
                    {
                        return false;
                    }
                }
                else
                {
                    // Changed -> event carries the kind and the new non-negative size (Req 14.7).
                    if (evt is null
                        || evt.Kind != change.Kind.ToString()
                        || evt.NewSize != expectedAfter
                        || evt.NewSize < 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }, iter: Iterations);
    }
}
