using Forge.Domain.Common;

namespace Forge.Domain.Spatial;

/// <summary>
/// The single grant point for path-segment reservations (Req 19.1, 19.3). The ledger
/// maps each <see cref="PathSegment"/> to an ordered list of held
/// <c>[EnterAt, ExitAt)</c> intervals with their owning <see cref="AgentId"/>, and is
/// the only place a reservation is accepted or rejected.
/// <para>
/// <b>Mutual exclusion (Req 19.3).</b> <see cref="TryReserve"/> grants a batch of timed
/// segments only when <em>none</em> of them overlaps an interval already held by a
/// <b>different</b> agent. Same-agent overlaps are permitted and ignored (an agent may
/// re-request or extend its own occupancy). A batch is all-or-nothing: on any conflict
/// the ledger rejects the whole request and reserves nothing, so the caller (Application)
/// can re-plan or hold the losing agent (Req 19.2).
/// </para>
/// <para>
/// <b>Determinism (Req 19.6, 28.9).</b> The ledger's accept/reject decision is a pure
/// function of its current held state and the request, so replaying an identical sequence
/// of <see cref="TryReserve"/>/<see cref="Release"/> operations yields identical outcomes.
/// The ledger reports the first conflicting request segment together with the blocking
/// agent so a caller can implement the design's <em>lower-<see cref="AgentId"/>-wins</em>
/// contention rule: when two agents contend, the caller reserves the lower-id agent first
/// (or releases the higher-id holder and re-grants), and the higher-id agent re-plans or
/// holds. Conflict scanning walks segments and intervals in a stable order and returns the
/// smallest-<see cref="AgentId"/> blocker for a given request segment, so the reported
/// conflict is itself deterministic.
/// </para>
/// </summary>
public sealed class ReservationLedger
{
    // Segment -> intervals held on it, in insertion order. A List keeps the scan order
    // stable and deterministic; conflict selection below breaks ties by ascending AgentId
    // so the reported blocker never depends on insertion order.
    private readonly Dictionary<PathSegment, List<PathReservation>> _bySegment = new();

    /// <summary>The total number of held reservations across all segments.</summary>
    public int Count
    {
        get
        {
            int total = 0;
            foreach (var held in _bySegment.Values)
            {
                total += held.Count;
            }

            return total;
        }
    }

    /// <summary>The number of distinct path segments that currently hold at least one reservation.</summary>
    public int ReservedSegmentCount => _bySegment.Count;

    /// <summary>
    /// All reservations held on <paramref name="segment"/>, in the order they were granted.
    /// Empty when nothing is held there. This is a read-only view for inspection/tests.
    /// </summary>
    public IReadOnlyList<PathReservation> ReservationsOn(PathSegment segment) =>
        _bySegment.TryGetValue(segment, out var held)
            ? held
            : Array.Empty<PathReservation>();

    /// <summary>
    /// The distinct endpoint cells (<c>From</c> and <c>To</c>) of every currently reserved segment,
    /// in ascending cell order (Req 19.5 congestion exposure). These are the "hot cells" the
    /// congestion projection surfaces. Deterministic: the result depends only on the held state, not
    /// on insertion or hash order. Empty when nothing is reserved.
    /// </summary>
    public IReadOnlyList<Cell> ReservedSegmentEndpoints()
    {
        var cells = new SortedSet<Cell>();
        foreach (var segment in _bySegment.Keys)
        {
            cells.Add(segment.From);
            cells.Add(segment.To);
        }

        return cells.ToArray();
    }

    /// <summary>
    /// Attempt to reserve every timed segment in <paramref name="segments"/> for
    /// <paramref name="agent"/> as a single all-or-nothing batch (Req 19.1, 19.3).
    /// <para>
    /// Grants only when no requested segment overlaps an interval already held by a
    /// different agent. On the first such conflict the request is rejected and nothing is
    /// reserved; the returned <see cref="ReservationOutcome"/> carries the conflicting
    /// request segment and the blocking agent so the caller can resolve contention
    /// deterministically (lower id wins) and re-plan/hold the loser (Req 19.2, 19.6).
    /// </para>
    /// </summary>
    public ReservationOutcome TryReserve(AgentId agent, IReadOnlyList<TimedSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // First pass: detect conflicts without mutating state (all-or-nothing).
        foreach (var requested in segments)
        {
            var blocker = FindBlocker(agent, requested);
            if (blocker is { } b)
            {
                return ReservationOutcome.Conflict(requested, b.Agent, b.Timed);
            }
        }

        // Second pass: commit. Same-agent overlaps are allowed; we simply append.
        foreach (var requested in segments)
        {
            if (!_bySegment.TryGetValue(requested.Segment, out var held))
            {
                held = new List<PathReservation>();
                _bySegment[requested.Segment] = held;
            }

            held.Add(new PathReservation(agent, requested));
        }

        return ReservationOutcome.Granted(agent, segments);
    }

    /// <summary>
    /// Drop <b>all</b> reservations held by every agent, resetting the ledger to empty. Reservations
    /// are intra-tick coordination only — they exist to stop two agents occupying the same path segment
    /// during a single tick's movement — so the tick pipeline clears the ledger at the start of each
    /// movement pass. Without this the ledger would accumulate every agent's segments across every tick
    /// unboundedly, degrading the conflict scan and eventually causing spurious cross-tick contention.
    /// </summary>
    public void Clear() => _bySegment.Clear();

    /// <summary>
    /// Drop every reservation held by <paramref name="agent"/> (Req 19.2 release path).
    /// Segments left with no reservations are removed from the map so the ledger stays
    /// compact and iteration order stays stable. Returns the number of reservations removed.
    /// </summary>
    public int Release(AgentId agent)
    {
        int removed = 0;
        var emptied = new List<PathSegment>();

        foreach (var (segment, held) in _bySegment)
        {
            int before = held.Count;
            held.RemoveAll(r => r.Agent.Equals(agent));
            removed += before - held.Count;

            if (held.Count == 0)
            {
                emptied.Add(segment);
            }
        }

        foreach (var segment in emptied)
        {
            _bySegment.Remove(segment);
        }

        return removed;
    }

    /// <summary>
    /// The lowest-<see cref="AgentId"/> reservation held by a <em>different</em> agent that
    /// overlaps <paramref name="requested"/>, or <see langword="null"/> when none conflicts.
    /// Choosing the smallest blocking id keeps the reported conflict deterministic regardless
    /// of insertion order and directly supports lower-id-wins resolution (Req 19.6).
    /// </summary>
    private PathReservation? FindBlocker(AgentId agent, TimedSegment requested)
    {
        if (!_bySegment.TryGetValue(requested.Segment, out var held))
        {
            return null;
        }

        PathReservation? best = null;
        foreach (var existing in held)
        {
            if (existing.Agent.Equals(agent))
            {
                continue; // same-agent overlaps are allowed
            }

            if (!requested.OverlapsInterval(existing.Timed))
            {
                continue;
            }

            if (best is null || existing.Agent.CompareTo(best.Agent) < 0)
            {
                best = existing;
            }
        }

        return best;
    }
}

/// <summary>
/// The result of <see cref="ReservationLedger.TryReserve"/> (Req 19.1, 19.2). Either the
/// whole batch was <see cref="IsGranted">granted</see>, or it was rejected with the
/// conflicting request segment and the blocking agent so the caller can resolve contention
/// deterministically and re-plan/hold the loser.
/// </summary>
public sealed record ReservationOutcome
{
    private ReservationOutcome(
        bool isGranted,
        AgentId agent,
        IReadOnlyList<TimedSegment> grantedSegments,
        TimedSegment? conflictingRequest,
        AgentId? blockingAgent,
        TimedSegment? blockingReservation)
    {
        IsGranted = isGranted;
        Agent = agent;
        GrantedSegments = grantedSegments;
        ConflictingRequest = conflictingRequest;
        BlockingAgent = blockingAgent;
        BlockingReservation = blockingReservation;
    }

    /// <summary>True when the entire requested batch was reserved.</summary>
    public bool IsGranted { get; }

    /// <summary>The agent the request was for.</summary>
    public AgentId Agent { get; }

    /// <summary>
    /// The timed segments that were reserved when <see cref="IsGranted"/> is true; empty on
    /// conflict (nothing is reserved on rejection).
    /// </summary>
    public IReadOnlyList<TimedSegment> GrantedSegments { get; }

    /// <summary>
    /// On conflict, the requested timed segment that could not be reserved; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public TimedSegment? ConflictingRequest { get; }

    /// <summary>
    /// On conflict, the agent whose held reservation blocked the request; otherwise
    /// <see langword="null"/>. The caller compares this against <see cref="Agent"/> to apply
    /// lower-id-wins (Req 19.6).
    /// </summary>
    public AgentId? BlockingAgent { get; }

    /// <summary>
    /// On conflict, the existing held reservation interval that blocked the request; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public TimedSegment? BlockingReservation { get; }

    internal static ReservationOutcome Granted(AgentId agent, IReadOnlyList<TimedSegment> segments) =>
        new(true, agent, segments, null, null, null);

    internal static ReservationOutcome Conflict(
        TimedSegment conflictingRequest,
        AgentId blockingAgent,
        TimedSegment blockingReservation) =>
        new(
            false,
            default,
            Array.Empty<TimedSegment>(),
            conflictingRequest,
            blockingAgent,
            blockingReservation);
}
