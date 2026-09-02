using Forge.Domain.Common;

namespace Forge.Domain.Docks;

/// <summary>
/// Grants a single-occupancy resource — a <see cref="DockBay"/> or <see cref="PickFace"/>,
/// keyed by <see cref="SingleOccupancyResourceId"/> — to <b>at most one</b> agent at a time
/// and queues the rest FIFO (Req 19.4). This is the domain-pure, deterministic core the
/// Application's reservation manager (task 17.1) wraps.
/// <para>
/// <b>Exclusivity (Req 19.4).</b> <see cref="TryAcquire"/> grants the resource when it is
/// free; otherwise the requesting agent is appended to that resource's FIFO wait queue and
/// the outcome reports it as queued. An agent that already holds the resource re-acquires it
/// idempotently; an agent already queued keeps its existing queue position (no duplicate).
/// </para>
/// <para>
/// <b>FIFO determinism.</b> Waiters are stored in a first-in-first-out queue, so
/// <see cref="Release"/> always grants the resource to the <em>earliest</em> queued waiter.
/// The queue order is arrival order, independent of hash iteration, so replaying an identical
/// sequence of acquire/release operations yields identical holders and queues.
/// </para>
/// </summary>
public sealed class SingleOccupancyRegistry
{
    private sealed class ResourceState
    {
        public AgentId? Holder;
        public readonly List<AgentId> Waiters = new();
    }

    private readonly Dictionary<SingleOccupancyResourceId, ResourceState> _byResource = new();

    /// <summary>
    /// Attempt to acquire <paramref name="resource"/> for <paramref name="agent"/> (Req 19.4).
    /// Grants immediately when the resource is free or already held by this agent; otherwise
    /// the agent is queued FIFO (or keeps its place if already queued) and the outcome reports
    /// its zero-based position in the wait queue.
    /// </summary>
    public ResourceOutcome TryAcquire(AgentId agent, SingleOccupancyResourceId resource)
    {
        var state = GetOrAdd(resource);

        if (state.Holder is null)
        {
            state.Holder = agent;
            return ResourceOutcome.Acquired(resource, agent);
        }

        if (state.Holder.Value.Equals(agent))
        {
            // Idempotent re-acquire by the current holder.
            return ResourceOutcome.Acquired(resource, agent);
        }

        int existing = state.Waiters.IndexOf(agent);
        if (existing >= 0)
        {
            // Already waiting; preserve FIFO position.
            return ResourceOutcome.Queued(resource, agent, existing);
        }

        state.Waiters.Add(agent);
        return ResourceOutcome.Queued(resource, agent, state.Waiters.Count - 1);
    }

    /// <summary>
    /// Release <paramref name="resource"/> from its current holder and grant it to the earliest
    /// queued waiter, if any (Req 19.4). Returns the newly-granted agent, or
    /// <see langword="null"/> when the queue was empty (the resource becomes free). If no agent
    /// currently holds the resource, this is a no-op returning <see langword="null"/>.
    /// </summary>
    public AgentId? Release(SingleOccupancyResourceId resource)
    {
        if (!_byResource.TryGetValue(resource, out var state) || state.Holder is null)
        {
            return null;
        }

        if (state.Waiters.Count == 0)
        {
            state.Holder = null;
            return null;
        }

        var next = state.Waiters[0];
        state.Waiters.RemoveAt(0);
        state.Holder = next;
        return next;
    }

    /// <summary>
    /// Remove <paramref name="agent"/> from the resource entirely: if it holds the resource the
    /// resource is released to the next waiter (returned); if it is only queued it is dropped
    /// from the queue and <see langword="null"/> is returned. Lets an agent that abandons a
    /// request stop blocking the resource without a full <see cref="Release"/> semantics change.
    /// </summary>
    public AgentId? Abandon(AgentId agent, SingleOccupancyResourceId resource)
    {
        if (!_byResource.TryGetValue(resource, out var state))
        {
            return null;
        }

        if (state.Holder is { } holder && holder.Equals(agent))
        {
            return Release(resource);
        }

        state.Waiters.Remove(agent);
        return null;
    }

    /// <summary>The agent currently holding <paramref name="resource"/>, or <see langword="null"/> if free.</summary>
    public AgentId? HolderOf(SingleOccupancyResourceId resource) =>
        _byResource.TryGetValue(resource, out var state) ? state.Holder : null;

    /// <summary>
    /// The agents queued for <paramref name="resource"/> in FIFO (arrival) order, for congestion
    /// exposure (task 22.1, Req 19.5). Empty when nobody is waiting.
    /// </summary>
    public IReadOnlyList<AgentId> WaitersOf(SingleOccupancyResourceId resource) =>
        _byResource.TryGetValue(resource, out var state)
            ? state.Waiters.ToArray()
            : Array.Empty<AgentId>();

    private ResourceState GetOrAdd(SingleOccupancyResourceId resource)
    {
        if (!_byResource.TryGetValue(resource, out var state))
        {
            state = new ResourceState();
            _byResource[resource] = state;
        }

        return state;
    }
}

/// <summary>
/// The result of <see cref="SingleOccupancyRegistry.TryAcquire"/> (Req 19.4). The agent either
/// <see cref="IsAcquired">acquired</see> the resource or was queued at a given FIFO position.
/// </summary>
public sealed record ResourceOutcome
{
    private ResourceOutcome(
        bool isAcquired,
        SingleOccupancyResourceId resource,
        AgentId agent,
        int queuePosition)
    {
        IsAcquired = isAcquired;
        Resource = resource;
        Agent = agent;
        QueuePosition = queuePosition;
    }

    /// <summary>True when the agent now holds the resource; false when it was queued.</summary>
    public bool IsAcquired { get; }

    /// <summary>The resource the request was for.</summary>
    public SingleOccupancyResourceId Resource { get; }

    /// <summary>The agent the request was for.</summary>
    public AgentId Agent { get; }

    /// <summary>
    /// When queued, the agent's zero-based position in the FIFO wait queue; <c>-1</c> when
    /// acquired. Position 0 means the agent is next to be granted on the next release.
    /// </summary>
    public int QueuePosition { get; }

    internal static ResourceOutcome Acquired(SingleOccupancyResourceId resource, AgentId agent) =>
        new(true, resource, agent, -1);

    internal static ResourceOutcome Queued(SingleOccupancyResourceId resource, AgentId agent, int position) =>
        new(false, resource, agent, position);
}
