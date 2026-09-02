using CsCheck;

using Forge.Domain.Common;
using Forge.Domain.Spatial;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 11: Path-reservation mutual exclusion and deterministic resolution
//
// Property 11 (design.md): "For any set of agents planning paths over the grid, after contention
// resolution no two agents SHALL hold a reservation on the same path segment during overlapping
// intervals, and identical simulation state SHALL resolve contention identically with ties broken
// by ascending agent identifier."
//
// The ReservationLedger is the single grant point: TryReserve is all-or-nothing and rejects any
// batch that overlaps an interval held by a different agent, so the mutual-exclusion invariant
// (Req 19.3) is preserved after every accepted operation. The ledger's accept/reject decision is a
// pure function of held state + request, so replaying an identical operation sequence reproduces the
// grant/reject decisions exactly (Req 19.6, 28.9). This test exercises both: it applies a random
// sequence of TryReserve/Release operations across several agents over a small shared segment set
// and asserts (a) the invariant holds after every step and (b) a fresh replay yields identical
// outcomes. A deterministic lower-id-wins resolution loop is also checked: replaying identical
// contention with the ledger reserving in ascending-AgentId order reproduces identical holders.
//
// Validates: Requirements 19.3, 19.6, 28.9
public sealed class PathReservationProperties
{
    // ≥100 iterations required by the spec.
    private const int Iterations = 100;

    private enum OpKind { Reserve, Release }

    private sealed record Op(OpKind Kind, int AgentIndex, IReadOnlyList<TimedSegment> Segments);

    private sealed record Scenario(IReadOnlyList<AgentId> Agents, IReadOnlyList<Op> Ops);

    // A tiny fixed pool of segments so contention actually happens. Distinct, deterministic cells.
    private static readonly PathSegment[] SegmentPool =
    {
        new(new Cell(0, 0), new Cell(1, 0)),
        new(new Cell(1, 0), new Cell(2, 0)),
        new(new Cell(2, 0), new Cell(2, 1)),
    };

    // A fixed base instant; intervals are expressed as integer "ticks" off this instant so overlap
    // is easy to reason about and generators stay in a small, dense space where overlaps are common.
    private static readonly DateTimeOffset Base = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int tick) => Base.AddSeconds(tick);

    private static readonly Gen<TimedSegment> GenTimedSegment =
        from segIndex in Gen.Int[0, SegmentPool.Length - 1]
        from enter in Gen.Int[0, 6]
        from length in Gen.Int[1, 4] // strictly positive so intervals are non-degenerate
        select new TimedSegment(SegmentPool[segIndex], At(enter), At(enter + length));

    private static Gen<Scenario> GenScenario(int agentCount)
    {
        // A stable, sorted set of distinct agent ids so ascending-id tie-breaks are meaningful and
        // the same identity set is reused across an operation sequence.
        var agents = new List<AgentId>(agentCount);
        for (int i = 0; i < agentCount; i++)
        {
            agents.Add(AgentId.New());
        }

        agents.Sort();

        Gen<Op> genOp =
            from kind in Gen.Int[0, 3] // ~1/4 releases, ~3/4 reserves
            from agentIndex in Gen.Int[0, agentCount - 1]
            from segs in GenTimedSegment.List[1, 4]
            select new Op(
                kind == 0 ? OpKind.Release : OpKind.Reserve,
                agentIndex,
                segs);

        return
            from ops in genOp.List[1, 20]
            select new Scenario(agents, ops);
    }

    // Req 19.3 / Property 11: after any sequence of TryReserve/Release, no two DIFFERENT agents hold
    // overlapping intervals on the same segment.
    [Fact]
    public void MutualExclusionInvariant_HoldsAfterEveryOperation()
    {
        GenScenario(agentCount: 4).Sample(scenario =>
        {
            var ledger = new ReservationLedger();

            foreach (var op in scenario.Ops)
            {
                var agent = scenario.Agents[op.AgentIndex];

                if (op.Kind == OpKind.Release)
                {
                    ledger.Release(agent);
                }
                else
                {
                    ledger.TryReserve(agent, op.Segments);
                }

                if (!NoCrossAgentOverlap(ledger))
                {
                    return false;
                }
            }

            return true;
        }, iter: Iterations);
    }

    // Req 19.6 / 28.9 / Property 11: replaying the identical operation sequence on a fresh ledger
    // yields identical grant/reject outcomes (deterministic resolution).
    [Fact]
    public void ReplayingIdenticalSequence_YieldsIdenticalOutcomes()
    {
        GenScenario(agentCount: 4).Sample(scenario =>
        {
            var first = ReplayGrantFlags(scenario);
            var second = ReplayGrantFlags(scenario);
            return first.SequenceEqual(second);
        }, iter: Iterations);
    }

    // Req 19.6 / Property 11: lower-AgentId-wins is deterministic. Two distinct agents each request
    // the SAME timed segment; whichever the caller reserves first (ascending id) wins, and the loser
    // is rejected with the winner reported as the blocker. Replaying reproduces the same winner.
    [Fact]
    public void LowerAgentIdWins_IsDeterministic()
    {
        var genContention =
            from a in GenTimedSegment
            select a;

        genContention.Sample(requested =>
        {
            // Two distinct, ordered agents.
            var x = AgentId.New();
            var y = AgentId.New();
            var lower = x.CompareTo(y) <= 0 ? x : y;
            var higher = x.CompareTo(y) <= 0 ? y : x;

            // Deterministic resolution: reserve in ascending-AgentId order.
            static (bool lowerGranted, bool higherGranted, AgentId? blocker) Resolve(
                AgentId lower, AgentId higher, TimedSegment seg)
            {
                var ledger = new ReservationLedger();
                var lowerOutcome = ledger.TryReserve(lower, new[] { seg });
                var higherOutcome = ledger.TryReserve(higher, new[] { seg });
                return (lowerOutcome.IsGranted, higherOutcome.IsGranted, higherOutcome.BlockingAgent);
            }

            var first = Resolve(lower, higher, requested);
            var second = Resolve(lower, higher, requested);

            // Lower id must win, higher id must be rejected and see the lower id as blocker.
            bool correct =
                first.lowerGranted
                && !first.higherGranted
                && first.blocker == lower;

            // And it must be reproducible.
            bool deterministic = first == second;

            return correct && deterministic;
        }, iter: Iterations);
    }

    // The invariant checker: scan all held reservations and confirm no two DIFFERENT agents overlap
    // on the same segment.
    private static bool NoCrossAgentOverlap(ReservationLedger ledger)
    {
        foreach (var segment in SegmentPool)
        {
            var held = ledger.ReservationsOn(segment);
            for (int i = 0; i < held.Count; i++)
            {
                for (int j = i + 1; j < held.Count; j++)
                {
                    var a = held[i];
                    var b = held[j];
                    if (a.Agent.Equals(b.Agent))
                    {
                        continue; // same-agent overlaps are allowed
                    }

                    if (a.Timed.OverlapsInterval(b.Timed))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static List<bool> ReplayGrantFlags(Scenario scenario)
    {
        var ledger = new ReservationLedger();
        var flags = new List<bool>(scenario.Ops.Count);

        foreach (var op in scenario.Ops)
        {
            var agent = scenario.Agents[op.AgentIndex];
            if (op.Kind == OpKind.Release)
            {
                ledger.Release(agent);
                flags.Add(true); // release always "succeeds"; record a stable marker
            }
            else
            {
                flags.Add(ledger.TryReserve(agent, op.Segments).IsGranted);
            }
        }

        return flags;
    }
}
