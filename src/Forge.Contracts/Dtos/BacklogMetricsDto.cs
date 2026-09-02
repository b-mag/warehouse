namespace Forge.Contracts.Dtos;

/// <summary>
/// Immutable projection of backlog and throughput metrics (Req 2.3, 23.4).
/// </summary>
public sealed record BacklogMetricsDto(
    int Receiving,
    int Outbound,
    double InboundThroughput,
    double OutboundThroughput,
    int DockContention,
    double DockUtilization);
