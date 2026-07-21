using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests concurrent request dispatch and end-to-end cancellation on the server (issue #43):
/// a slow handler must not block fast ones, and notifications/cancelled must cancel the
/// matching in-flight handler promptly.
/// </summary>
public class ServerConcurrencyCancellationTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly IClientTransport _client;
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(15));

        private Harness(IClientTransport client) => _client = client;

        public static async Task<Harness> StartAsync(Action<McpServer> configure)
        {
            var (client, serverTransport) = InMemoryTransport.CreatePair();
            var server = new McpServer(serverTransport);
            configure(server);
            _ = server.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            await client.ConnectAsync();
            var h = new Harness(client);
            await h.InitializeAsync();
            return h;
        }

        private async Task InitializeAsync()
        {
            await _client.SendAsync(new JsonRpcRequest
            {
                Id = 1,
                Method = McpMethods.Initialize,
                Params = McpJsonDefaults.ToElement(new InitializeParams
                {
                    ProtocolVersion = McpSession.LatestProtocolVersion,
                    Capabilities = new ClientCapabilities(),
                    ClientInfo = new Implementation("c", "1.0")
                })
            }, Cts.Token);
            await ReadResponseAsync(); // init response
            await _client.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsInitialized }, Cts.Token);
        }

        public Task CallToolAsync(int id, string name) => _client.SendAsync(new JsonRpcRequest
        {
            Id = id,
            Method = McpMethods.ToolsCall,
            Params = McpJsonDefaults.ToElement(new { name })
        }, Cts.Token);

        public Task CancelAsync(int requestId) => _client.SendAsync(
            new JsonRpcNotification
            {
                Method = McpMethods.NotificationsCancelled,
                Params = McpJsonDefaults.ToElement(new CancelledParams { RequestId = requestId })
            }, Cts.Token);

        public async Task<JsonRpcResponse> ReadResponseAsync()
        {
            await foreach (var msg in _client.Messages.WithCancellation(Cts.Token))
                if (msg is JsonRpcResponse r) return r;
            throw new InvalidOperationException("No response received.");
        }

        public ValueTask DisposeAsync()
        {
            Cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task SlowHandler_DoesNotBlock_FastHandler()
    {
        var slowRelease = new TaskCompletionSource();
        var slowStarted = new TaskCompletionSource();

        await using var h = await Harness.StartAsync(s =>
        {
            s.AddTool("slow", "d", async (_, _) =>
            {
                slowStarted.TrySetResult();
                await slowRelease.Task;
                return CallToolResult.Text("slow-done");
            });
            s.AddTool("fast", "d", (_, _) => Task.FromResult(CallToolResult.Text("fast-done")));
        });

        await h.CallToolAsync(2, "slow");
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // slow is running
        await h.CallToolAsync(3, "fast");

        // The fast response must arrive while the slow handler is still blocked.
        var first = await h.ReadResponseAsync();
        Assert.Equal((RequestId)3, first.Id);
        Assert.False(first.IsError);

        // Now release the slow handler and read its response.
        slowRelease.SetResult();
        var second = await h.ReadResponseAsync();
        Assert.Equal((RequestId)2, second.Id);
    }

    [Fact]
    public async Task CancelledNotification_CancelsRunningHandler()
    {
        var started = new TaskCompletionSource();
        var cancelledObserved = new TaskCompletionSource<bool>();

        await using var h = await Harness.StartAsync(s =>
            s.AddTool("long", "d", async (_, ct) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelledObserved.TrySetResult(true);
                    throw;
                }
                return CallToolResult.Text("done");
            }));

        await h.CallToolAsync(2, "long");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await h.CancelAsync(2);

        Assert.True(await cancelledObserved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
