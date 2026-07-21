using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Client;

/// <summary>
/// Tests that the client sends a cancellation notification when caller cancellation or a request
/// timeout stops it waiting, and leaves no tracker entries behind (issue #43).
/// </summary>
public class ClientCancellationTimeoutTests
{
    /// <summary>
    /// A mock server that answers initialize, never answers other requests, and records the
    /// cancellation notification and when it first sees a non-initialize request.
    /// </summary>
    private sealed class SilentServer
    {
        public TaskCompletionSource<CancelledParams> Cancelled { get; } = new();
        public TaskCompletionSource RequestSeen { get; } = new();

        public void Run(IServerTransport transport, CancellationToken ct) => _ = Task.Run(async () =>
        {
            await transport.StartAsync(ct);
            await foreach (var msg in transport.Messages.WithCancellation(ct))
            {
                switch (msg)
                {
                    case JsonRpcRequest { Method: McpMethods.Initialize } init:
                        await transport.SendAsync(JsonRpcResponse.Success(init.Id, McpJsonDefaults.ToElement(
                            new InitializeResult
                            {
                                ProtocolVersion = McpSession.LatestProtocolVersion,
                                Capabilities = new ServerCapabilities(),
                                ServerInfo = new Implementation("s", "1.0")
                            })), ct);
                        break;
                    case JsonRpcRequest:
                        RequestSeen.TrySetResult(); // deliberately no response
                        break;
                    case JsonRpcNotification { Method: McpMethods.NotificationsCancelled } n:
                        Cancelled.TrySetResult(n.GetParams<CancelledParams>()!);
                        break;
                }
            }
        }, ct);
    }

    [Fact]
    public async Task Timeout_SendsCancellation_AndLeavesNoTrackerEntries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var mock = new SilentServer();
        mock.Run(serverTransport, cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport,
            new McpClientOptions { RequestTimeout = TimeSpan.FromMilliseconds(300) },
            cancellationToken: cts.Token);

        await Assert.ThrowsAsync<TimeoutException>(() => client.PingAsync(cts.Token));

        var cancelled = await mock.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(cancelled);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task CallerCancellation_SendsCancellation_AndLeavesNoTrackerEntries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var mock = new SilentServer();
        mock.Run(serverTransport, cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport,
            new McpClientOptions { RequestTimeout = TimeSpan.FromSeconds(30) },
            cancellationToken: cts.Token);

        using var callCts = new CancellationTokenSource();
        var call = client.PingAsync(callCts.Token);

        await mock.RequestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)); // request is in flight
        callCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await mock.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, client.PendingRequestCount);
    }
}
