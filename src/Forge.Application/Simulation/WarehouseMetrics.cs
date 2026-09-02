using Forge.Application.Docks;

using Forge.Contracts.Dtos;

using Forge.Domain.Common;
using Forge.Domain.Docks;
using Forge.Domain.Events;
using Forge.Domain.Spatial;

namespace Forge.Application.Simulation;

/// <summary>
/// Which backlog a <see cref="WarehouseMetrics"/> change refers to. The string form is carried on
/// the emitted <see cref="BacklogChanged"/> domain event's <c>Kind</c> so downstream consumers can
/// tell receiving from outbound (Req 14.7, 27.4).
/// </summary>
public enum BacklogKind
{
    /// <summary>The inbound receiving backlog: arrivals awaiting put-away/storage capacity (Req 14.3).</summary>
    Receiving,

    /// <summary>The outbound backlog: demand awaiting pickable inventory/labor (Req 14.4).</summary>
    Outbound,
}

/// <summary>
/// The Application-layer metrics component of the tick pipeline's <b>Metrics</b> stage (design.md
/// "per-tick rule stages", stage 5). It owns the two observable backlogs, the inbound/outbound
/// throughput rates, and the reservation-congestion projection that the snapshot exposes
/// (Req 14.3, 14.4, 14.5, 14.7, 19.5).
///
/// <para>
/// <b>Backlogs (Req 14.3, 14.4, 14.7).</b> A receiving backlog accumulates while inbound arrivals
/// outpace the rate put-away plus available storage capacity can absorb; an outbound backlog
/// accumulates while outbound demand outpaces pickable non-expired inventory or available labor.
/// The tick pipeline reports absorbed-vs-blocked counts by calling <see cref="IncrementReceiving"/>
/// / <see cref="DecrementReceiving"/> (and the outbound equivalents), or <see cref="SetReceiving"/>
/// / <see cref="SetOutbound"/> to recompute a size directly. Every mutator <b>clamps at zero</b> so
/// the exposed sizes are always non-negative integers, and returns the <see cref="BacklogChanged"/>
/// domain event to publish <em>only when the size actually changed</em> (Req 14.7).
/// </para>
///
/// <para>
/// <b>Throughput windowing (Req 14.5).</b> Throughput is <em>gel-lots processed per unit of
/// simulated time</em>. The pipeline records processed lot counts against elapsed simulated time via
/// <see cref="RecordInboundProcessed"/> / <see cref="RecordOutboundProcessed"/>, which accumulate a
/// running <c>(lots, simulated-seconds)</c> window. <see cref="InboundThroughput"/> /
/// <see cref="OutboundThroughput"/> then return <c>lots / elapsed-simulated-seconds</c> — a simple
/// cumulative average rate over the whole recorded window. When no simulated time has elapsed the
/// rate is <c>0</c> (rather than dividing by zero). This cumulative window is deterministic: identical
/// recorded sequences yield an identical rate, and callers that want a sliding window can
/// <see cref="ResetThroughput"/> at window boundaries.
/// </para>
///
/// <para>
/// <b>Congestion (Req 19.5).</b> <see cref="GetCongestion"/> derives a domain-pure
/// <see cref="CongestionSnapshot"/> from held <see cref="ReservationLedger"/> reservations plus the
/// agents queued in a <see cref="SingleOccupancyRegistry"/>, deterministically. This mirrors the
/// <c>IReservationManager.GetCongestion</c> seam; mapping to the transport <c>CongestionDto</c>
/// happens later at the Api boundary.
/// </para>
///
/// <para>
/// This type lives in <c>Forge.Application</c> and depends only on <c>Forge.Domain</c> +
/// <c>Forge.Contracts</c>, preserving the layer boundary. It holds no clock and reads no wall time —
/// the caller supplies the simulated timestamp stamped onto each <see cref="BacklogChanged"/> event,
/// keeping the component deterministic.
/// </para>
/// </summary>
public sealed class WarehouseMetrics
{
    private int _receiving;
    private int _outbound;

    // Cumulative throughput windows: lots processed and simulated seconds elapsed since the last
    // reset. Rate = lots / seconds. Kept as doubles so fractional-second deltas accumulate exactly.
    private double _inboundLots;
    private double _inboundSeconds;
    private double _outboundLots;
    private double _outboundSeconds;

    /// <summary>The current receiving backlog size, always a non-negative integer (Req 14.3).</summary>
    public int Receiving => _receiving;

    /// <summary>The current outbound backlog size, always a non-negative integer (Req 14.4).</summary>
    public int Outbound => _outbound;

    /// <summary>
    /// The current backlog size for <paramref name="kind"/>, always non-negative.
    /// </summary>
    public int BacklogOf(BacklogKind kind) =>
        kind == BacklogKind.Receiving ? _receiving : _outbound;

    /// <summary>
    /// Inbound throughput as gel-lots processed per simulated second over the recorded window
    /// (Req 14.5). Returns <c>0</c> when no simulated time has been recorded.
    /// </summary>
    public double InboundThroughput =>
        _inboundSeconds > 0d ? _inboundLots / _inboundSeconds : 0d;

    /// <summary>
    /// Outbound throughput as gel-lots processed per simulated second over the recorded window
    /// (Req 14.5). Returns <c>0</c> when no simulated time has been recorded.
    /// </summary>
    public double OutboundThroughput =>
        _outboundSeconds > 0d ? _outboundLots / _outboundSeconds : 0d;

    // ---- Backlog mutators (Req 14.7). Each clamps at zero and returns an event only on change. ----

    /// <summary>
    /// Add <paramref name="count"/> blocked arrivals to the receiving backlog (Req 14.3, 14.6).
    /// Negative counts are treated as a decrement. Returns the <see cref="BacklogChanged"/> event to
    /// publish, or <see langword="null"/> when the clamped size did not change.
    /// </summary>
    public BacklogChanged? IncrementReceiving(int count, DateTimeOffset at) =>
        SetReceiving(_receiving + count, at);

    /// <summary>
    /// Remove <paramref name="count"/> absorbed arrivals from the receiving backlog (Req 14.3).
    /// The result is clamped at zero so the backlog never goes negative. Returns the event to publish
    /// or <see langword="null"/> when unchanged.
    /// </summary>
    public BacklogChanged? DecrementReceiving(int count, DateTimeOffset at) =>
        SetReceiving(_receiving - count, at);

    /// <summary>
    /// Set the receiving backlog to <paramref name="size"/>, clamped at zero (Req 14.3, 14.7).
    /// Returns the <see cref="BacklogChanged"/> event to publish when the clamped value differs from
    /// the current size, or <see langword="null"/> when it is unchanged.
    /// </summary>
    public BacklogChanged? SetReceiving(int size, DateTimeOffset at)
    {
        int clamped = size < 0 ? 0 : size;
        if (clamped == _receiving)
        {
            return null;
        }

        _receiving = clamped;
        return new BacklogChanged(BacklogKind.Receiving.ToString(), clamped, at);
    }

    /// <summary>
    /// Add <paramref name="count"/> to the outbound backlog (Req 14.4). Negative counts decrement.
    /// Returns the event to publish or <see langword="null"/> when the clamped size did not change.
    /// </summary>
    public BacklogChanged? IncrementOutbound(int count, DateTimeOffset at) =>
        SetOutbound(_outbound + count, at);

    /// <summary>
    /// Remove <paramref name="count"/> from the outbound backlog (Req 14.4), clamped at zero.
    /// Returns the event to publish or <see langword="null"/> when unchanged.
    /// </summary>
    public BacklogChanged? DecrementOutbound(int count, DateTimeOffset at) =>
        SetOutbound(_outbound - count, at);

    /// <summary>
    /// Set the outbound backlog to <paramref name="size"/>, clamped at zero (Req 14.4, 14.7).
    /// Returns the <see cref="BacklogChanged"/> event when the clamped value differs from the current
    /// size, or <see langword="null"/> when unchanged.
    /// </summary>
    public BacklogChanged? SetOutbound(int size, DateTimeOffset at)
    {
        int clamped = size < 0 ? 0 : size;
        if (clamped == _outbound)
        {
            return null;
        }

        _outbound = clamped;
        return new BacklogChanged(BacklogKind.Outbound.ToString(), clamped, at);
    }

    /// <summary>
    /// Apply a change of <paramref name="delta"/> to the backlog identified by
    /// <paramref name="kind"/> (Req 14.7), clamped at zero. Convenience over the kind-specific
    /// mutators for callers driving both backlogs uniformly.
    /// </summary>
    public BacklogChanged? Apply(BacklogKind kind, int delta, DateTimeOffset at) =>
        kind == BacklogKind.Receiving
            ? SetReceiving(_receiving + delta, at)
            : SetOutbound(_outbound + delta, at);

    // ---- Throughput recording (Req 14.5). ----

    /// <summary>
    /// Record that <paramref name="lots"/> inbound gel-lots were processed over
    /// <paramref name="simulatedSeconds"/> of simulated time (Req 14.5). Non-positive
    /// <paramref name="simulatedSeconds"/> only accumulates the lot count; the elapsed-time window is
    /// never reduced. Negative lot counts are ignored.
    /// </summary>
    public void RecordInboundProcessed(int lots, double simulatedSeconds)
    {
        if (lots > 0)
        {
            _inboundLots += lots;
        }

        if (simulatedSeconds > 0d)
        {
            _inboundSeconds += simulatedSeconds;
        }
    }

    /// <summary>
    /// Record that <paramref name="lots"/> outbound gel-lots were processed over
    /// <paramref name="simulatedSeconds"/> of simulated time (Req 14.5). Same accumulation rules as
    /// <see cref="RecordInboundProcessed"/>.
    /// </summary>
    public void RecordOutboundProcessed(int lots, double simulatedSeconds)
    {
        if (lots > 0)
        {
            _outboundLots += lots;
        }

        if (simulatedSeconds > 0d)
        {
            _outboundSeconds += simulatedSeconds;
        }
    }

    /// <summary>
    /// Reset the throughput windows to zero so a caller can measure a fresh sliding interval. Leaves
    /// the backlog sizes untouched.
    /// </summary>
    public void ResetThroughput()
    {
        _inboundLots = 0d;
        _inboundSeconds = 0d;
        _outboundLots = 0d;
        _outboundSeconds = 0d;
    }

    // ---- Congestion projection (Req 19.5). ----

    /// <summary>
    /// Derive a <see cref="CongestionSnapshot"/> from the held path-segment reservations in
    /// <paramref name="ledger"/> plus the agents queued in <paramref name="registry"/> across the
    /// given <paramref name="resources"/> (Req 19.5). Deterministic: hot cells are the distinct
    /// endpoints of currently reserved segments in ascending cell order.
    /// </summary>
    public static CongestionSnapshot GetCongestion(
        ReservationLedger ledger,
        SingleOccupancyRegistry registry,
        IReadOnlyList<SingleOccupancyResourceId> resources)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resources);

        int queuedAgents = 0;
        foreach (var resource in resources)
        {
            queuedAgents += registry.WaitersOf(resource).Count;
        }

        return BuildCongestion(ledger.ReservedSegmentCount, ledger.ReservedSegmentEndpoints(), queuedAgents);
    }

    /// <summary>
    /// Build a congestion snapshot directly from already-exposed counts and hot cells (Req 19.5), for
    /// callers that have their own reservation/queue projections. Hot cells are ordered ascending and
    /// de-duplicated so the snapshot is deterministic.
    /// </summary>
    public static CongestionSnapshot BuildCongestion(
        int reservedSegments,
        IReadOnlyList<Cell> hotCells,
        int queuedAgents)
    {
        ArgumentNullException.ThrowIfNull(hotCells);

        var ordered = hotCells
            .Distinct()
            .OrderBy(c => c)
            .ToArray();

        return new CongestionSnapshot(
            reservedSegments < 0 ? 0 : reservedSegments,
            queuedAgents < 0 ? 0 : queuedAgents,
            ordered);
    }

    // ---- Snapshot assembly (Req 14.3–14.5, 17.3–17.4). ----

    /// <summary>
    /// Assemble the transport <see cref="BacklogMetricsDto"/> for a snapshot: the two backlog sizes,
    /// the inbound/outbound throughput rates, and — from <paramref name="dockScheduler"/> — the dock
    /// contention backlog and dock utilization for <paramref name="dockBay"/> (Req 14.3–14.5, 17.3,
    /// 17.4). Read-only: assembling a snapshot never mutates metrics state.
    /// </summary>
    public BacklogMetricsDto ToDto(DockScheduler dockScheduler, DockBayId dockBay)
    {
        ArgumentNullException.ThrowIfNull(dockScheduler);

        return new BacklogMetricsDto(
            Receiving: _receiving,
            Outbound: _outbound,
            InboundThroughput: InboundThroughput,
            OutboundThroughput: OutboundThroughput,
            DockContention: dockScheduler.Backlog,
            DockUtilization: dockScheduler.UtilizationOf(dockBay));
    }
}
