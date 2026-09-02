using CsCheck;

using Forge.Domain.Common;
using Forge.Domain.Docks;

namespace Forge.Tests.Properties;

// Feature: nutrient-forge, Property 12: Single-occupancy resource exclusivity
//
// Property 12 (design.md): "For any set of agents contending for a dock bay or pick face, at most
// one agent SHALL hold the resource at any instant and the remaining agents SHALL be queued until
// it is released."
//
// The SingleOccupancyRegistry is the domain-pure core: TryAcquire grants the resource only when it
// is free (otherwise the agent joins a FIFO queue), and Release grants the resource to the earliest
// queued waiter. This test drives a random sequence of acquire/release operations by several agents
// against a single resource and asserts, after every step, (a) at most one holder exists, (b) the
// holder is never also queued, (c) waiters carry no duplicates, and (d) on release the earliest
// queued waiter (FIFO) becomes the new holder. FIFO order is arrival order, so the outcome is
// deterministic across replays.
//
// Validates: Requirements 19.4
public sealed class SingleOccupancyProperties
{
    // ≥100 iterations required by the spec.
    private const int Iterations = 100;

    private enum OpKind { Acquire, Release }

    private sealed record Op(OpKind Kind, int AgentIndex);

    private sealed record Scenario(IReadOnlyList<AgentId> Agents, IReadOnlyList<Op> Ops);

    // A single shared resource so every operation contends for the same thing.
    private static readonly SingleOccupancyResourceId Resource =
        SingleOccupancyResourceId.ForDockBay(DockBayId.New());

    private static Gen<Scenario> GenScenario(int agentCount)
    {
        var agents = new List<AgentId>(agentCount);
        for (int i = 0; i < agentCount; i++)
        {
            agents.Add(AgentId.New());
        }

        Gen<Op> genOp =
            from kind in Gen.Int[0, 2] // ~1/3 releases, ~2/3 acquires
            from agentIndex in Gen.Int[0, agentCount - 1]
            select new Op(kind == 0 ? OpKind.Release : OpKind.Acquire, agentIndex);

        return
            from ops in genOp.List[1, 25]
            select new Scenario(agents, ops);
    }

    // Req 19.4 / Property 12: at most one holder at any instant, and structural queue invariants hold
    // after every operation.
    [Fact]
    public void AtMostOneHolder_AndQueueInvariants_HoldAfterEveryOperation()
    {
        GenScenario(agentCount: 5).Sample(scenario =>
        {
            var registry = new SingleOccupancyRegistry();

            foreach (var op in scenario.Ops)
            {
                var agent = scenario.Agents[op.AgentIndex];

                if (op.Kind == OpKind.Acquire)
                {
                    var outcome = registry.TryAcquire(agent, Resource);

                    // If acquired, this agent must be the holder; if queued, it must appear in the queue.
                    if (outcome.IsAcquired)
                    {
                        if (registry.HolderOf(Resource) != agent)
                        {
                            return false;
                        }
                    }
                    else if (!registry.WaitersOf(Resource).Contains(agent))
                    {
                        return false;
                    }
                }
                else
                {
                    registry.Release(Resource);
                }

                if (!QueueInvariantsHold(registry))
                {
                    return false;
                }
            }

            return true;
        }, iter: Iterations);
    }

    // Req 19.4 / Property 12: on release, the EARLIEST queued waiter (FIFO) becomes the new holder,
    // and the queue shrinks by exactly that agent from the front.
    [Fact]
    public void Release_GrantsEarliestQueuedWaiter_Fifo()
    {
        GenScenario(agentCount: 5).Sample(scenario =>
        {
            var registry = new SingleOccupancyRegistry();

            foreach (var op in scenario.Ops)
            {
                var agent = scenario.Agents[op.AgentIndex];

                if (op.Kind == OpKind.Acquire)
                {
                    registry.TryAcquire(agent, Resource);
                    continue;
                }

                // Snapshot the queue before releasing.
                var before = registry.WaitersOf(Resource);
                var expectedNext = before.Count > 0 ? before[0] : (AgentId?)null;

                var granted = registry.Release(Resource);

                if (granted != expectedNext)
                {
                    return false;
                }

                if (granted is { } g)
                {
                    // The granted agent must now hold the resource and be gone from the queue front.
                    if (registry.HolderOf(Resource) != g)
                    {
                        return false;
                    }

                    var after = registry.WaitersOf(Resource);
                    var expectedAfter = before.Skip(1).ToArray();
                    if (!after.SequenceEqual(expectedAfter))
                    {
                        return false;
                    }
                }
                else
                {
                    // Empty queue -> resource becomes free.
                    if (registry.HolderOf(Resource) is not null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }, iter: Iterations);
    }

    private static bool QueueInvariantsHold(SingleOccupancyRegistry registry)
    {
        var holder = registry.HolderOf(Resource);
        var waiters = registry.WaitersOf(Resource);

        // Holder must not also be queued.
        if (holder is { } h && waiters.Contains(h))
        {
            return false;
        }

        // No duplicate waiters.
        if (waiters.Distinct().Count() != waiters.Count)
        {
            return false;
        }

        // Nobody can be queued while the resource is free.
        if (holder is null && waiters.Count > 0)
        {
            return false;
        }

        return true;
    }
}
