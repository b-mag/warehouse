using Forge.Api.Hubs;
using Forge.Application.Abstractions;
using Forge.Application.Abstractions.Commands;
using Forge.Contracts.Dtos;
using Forge.Domain.Common;

using Microsoft.AspNetCore.SignalR;

using Xunit;

namespace Forge.Tests.Api;

/// <summary>
/// Unit tests for the <see cref="SimulationHub"/> (task 33.1). On connect the hub reads the current
/// <see cref="SimulationSnapshotDto"/> from the core gateway and sends it to the connecting client as
/// the initial snapshot message (<see cref="SimulationHub.SnapshotMethod"/>) — seeding new clients
/// without a running SignalR server.
/// Validates: Requirements 23.1, 23.3.
/// </summary>
public sealed class SimulationHubTests
{
    /// <summary>A gateway whose only exercised member is the read-only snapshot query.</summary>
    private sealed class FakeGateway : IWarehouseCommandGateway
    {
        private readonly SimulationSnapshotDto _snapshot;

        public FakeGateway(SimulationSnapshotDto snapshot) => _snapshot = snapshot;

        public int GetSnapshotCalls { get; private set; }

        public Task<SimulationSnapshotDto> GetSnapshotAsync(CancellationToken ct = default)
        {
            GetSnapshotCalls++;
            return Task.FromResult(_snapshot);
        }

        public Task<Result<ColonyOrderId>> CreateColonyOrderAsync(CreateColonyOrderCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Result> RecordInboundGelReceiptAsync(RecordInboundGelReceiptCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Result> RecordTemperatureReadingAsync(RecordTemperatureReadingCommand cmd, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Result> ApplyTickRulesAsync(TimeSpan simDelta, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>A client proxy recording the message sent to the caller.</summary>
    private sealed class RecordingClientProxy : IClientProxy
    {
        public string? LastMethod { get; private set; }
        public object?[]? LastArgs { get; private set; }
        public int SendCount { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            LastMethod = method;
            LastArgs = args;
            SendCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Hub-caller clients whose <see cref="Caller"/> is the recording proxy.</summary>
    private sealed class FakeCallerClients : IHubCallerClients
    {
        private readonly IClientProxy _caller;

        public FakeCallerClients(IClientProxy caller) => _caller = caller;

        public IClientProxy Caller => _caller;
        public IClientProxy Others => throw new NotSupportedException();
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy OthersInGroup(string groupName) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    /// <summary>A minimal caller context exposing a stable connection id and an abort token.</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public override string ConnectionId => "conn-1";
        public override string? UserIdentifier => null;
        public override System.Security.Claims.ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } =
            new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static SimulationSnapshotDto SampleSnapshot() => new(
        Zones: [],
        Lots: [],
        Agents: [],
        Starships: [],
        Metrics: new BacklogMetricsDto(0, 0, 0.0, 0.0, 0, 0.0),
        Parameters: new OperatorParameterStateDto(1.0, 5, 2, 3.0, 1.0, "velocity"));

    [Fact]
    public async Task OnConnected_sends_snapshot_to_caller()
    {
        var snapshot = SampleSnapshot();
        var gateway = new FakeGateway(snapshot);
        var caller = new RecordingClientProxy();

        var hub = new SimulationHub(gateway)
        {
            Clients = new FakeCallerClients(caller),
            Context = new FakeHubCallerContext(),
        };

        await hub.OnConnectedAsync();

        Assert.Equal(1, gateway.GetSnapshotCalls);
        Assert.Equal(1, caller.SendCount);
        Assert.Equal(SimulationHub.SnapshotMethod, caller.LastMethod);
        var arg = Assert.Single(caller.LastArgs!);
        Assert.Same(snapshot, arg);
    }

    [Fact]
    public void SnapshotMethod_is_the_stable_client_method_name()
    {
        Assert.Equal("Snapshot", SimulationHub.SnapshotMethod);
    }
}
