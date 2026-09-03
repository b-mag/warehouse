using Forge.Api.Hubs;
using Forge.Api.RealTime;

using Microsoft.AspNetCore.SignalR;

using Xunit;

namespace Forge.Tests.Api;

/// <summary>
/// Unit tests for the <see cref="SignalRClientNotifier"/> (task 33.1). The notifier is the Api-layer
/// adapter that satisfies the <c>ISimulationClientNotifier</c> seam by forwarding each push to
/// <c>IHubContext&lt;SimulationHub&gt;.Clients.All.SendAsync(eventName, payload, ct)</c> — delivering a
/// single named message carrying the payload to all clients, without blocking, and surfacing a
/// genuinely unavailable channel as a faulted task the publisher's pump swallows.
/// Validates: Requirements 20.9, 23.2, 23.4.
/// </summary>
public sealed class SignalRClientNotifierTests
{
    /// <summary>A fake client proxy that records each <c>SendAsync</c> and can be told to fault.</summary>
    private sealed class RecordingClientProxy : IClientProxy
    {
        private readonly Exception? _fault;

        public RecordingClientProxy(Exception? fault = null) => _fault = fault;

        public string? LastMethod { get; private set; }
        public object?[]? LastArgs { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public int SendCount { get; private set; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            LastMethod = method;
            LastArgs = args;
            LastToken = cancellationToken;
            SendCount++;

            return _fault is null ? Task.CompletedTask : Task.FromException(_fault);
        }
    }

    /// <summary>A fake hub-clients accessor whose <see cref="All"/> returns the recording proxy.</summary>
    private sealed class FakeHubClients : IHubClients
    {
        private readonly RecordingClientProxy _all;

        public FakeHubClients(RecordingClientProxy all) => _all = all;

        public IClientProxy All => _all;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _all;
        public IClientProxy Client(string connectionId) => _all;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _all;
        public IClientProxy Group(string groupName) => _all;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _all;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _all;
        public IClientProxy User(string userId) => _all;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _all;
    }

    /// <summary>A fake hub context exposing the fake clients; group manager is unused here.</summary>
    private sealed class FakeHubContext : IHubContext<SimulationHub>
    {
        public FakeHubContext(RecordingClientProxy all) => Clients = new FakeHubClients(all);

        public IHubClients Clients { get; }
        public IGroupManager Groups => throw new NotSupportedException();
    }

    [Fact]
    public async Task NotifyAsync_forwards_event_name_and_payload_to_all_clients()
    {
        var proxy = new RecordingClientProxy();
        var notifier = new SignalRClientNotifier(new FakeHubContext(proxy));
        var payload = new { LotId = Guid.NewGuid() };
        using var cts = new CancellationTokenSource();

        await notifier.NotifyAsync("LotExpired", payload, cts.Token);

        Assert.Equal(1, proxy.SendCount);
        Assert.Equal("LotExpired", proxy.LastMethod);
        var args = Assert.Single(proxy.LastArgs!);
        Assert.Same(payload, args);
        Assert.Equal(cts.Token, proxy.LastToken);
    }

    [Fact]
    public async Task NotifyAsync_delivers_exactly_one_message_per_call()
    {
        var proxy = new RecordingClientProxy();
        var notifier = new SignalRClientNotifier(new FakeHubContext(proxy));

        await notifier.NotifyAsync("BacklogChanged", new { Kind = "inbound", NewSize = 3 });
        await notifier.NotifyAsync("OperatorParameterChanged", new { SimSpeed = 2.0 });

        Assert.Equal(2, proxy.SendCount);
        Assert.Equal("OperatorParameterChanged", proxy.LastMethod);
    }

    [Fact]
    public async Task NotifyAsync_faults_when_channel_is_unavailable()
    {
        var proxy = new RecordingClientProxy(new InvalidOperationException("channel unavailable"));
        var notifier = new SignalRClientNotifier(new FakeHubContext(proxy));

        // A genuinely unavailable channel surfaces as a faulted task; the publisher's pump swallows it.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => notifier.NotifyAsync("DockBlocked", new { DockBayId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task NotifyAsync_rejects_null_payload_and_empty_event_name()
    {
        var proxy = new RecordingClientProxy();
        var notifier = new SignalRClientNotifier(new FakeHubContext(proxy));

        await Assert.ThrowsAsync<ArgumentNullException>(() => notifier.NotifyAsync("X", null!));
        await Assert.ThrowsAsync<ArgumentException>(() => notifier.NotifyAsync("", new object()));
    }
}
