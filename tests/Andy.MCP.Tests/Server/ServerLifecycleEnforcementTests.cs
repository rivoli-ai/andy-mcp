using Andy.MCP.Protocol;
using Andy.MCP.Server;
using Andy.MCP.Transport;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests MCP lifecycle enforcement on the server (issue #42): operations other than ping are
/// rejected before the client's notifications/initialized, and initialize is accepted only once.
/// </summary>
public class ServerLifecycleEnforcementTests
{
    private sealed class Harness : IAsyncDisposable
    {
        private readonly IClientTransport _client;
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(15));

        private Harness(IClientTransport client) => _client = client;

        public static async Task<Harness> StartAsync(Action<McpServer>? configure = null)
        {
            var (client, serverTransport) = InMemoryTransport.CreatePair();
            var server = new McpServer(serverTransport);
            configure?.Invoke(server);
            _ = server.RunAsync(new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
            await client.ConnectAsync();
            var h = new Harness(client);
            return h;
        }

        public Task SendAsync(JsonRpcMessage message) => _client.SendAsync(message, Cts.Token);

        public async Task<JsonRpcResponse> ReadResponseAsync()
        {
            await foreach (var msg in _client.Messages.WithCancellation(Cts.Token))
                if (msg is JsonRpcResponse r) return r;
            throw new InvalidOperationException("No response received.");
        }

        public Task SendInitializeAsync(int id = 1) => SendAsync(new JsonRpcRequest
        {
            Id = id,
            Method = McpMethods.Initialize,
            Params = McpJsonDefaults.ToElement(new InitializeParams
            {
                ProtocolVersion = McpSession.LatestProtocolVersion,
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation("c", "1.0")
            })
        });

        public Task SendInitializedAsync() =>
            SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsInitialized });

        public ValueTask DisposeAsync()
        {
            Cts.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Operation_BeforeInitialized_IsRejected()
    {
        await using var h = await Harness.StartAsync();
        await h.SendInitializeAsync();
        await h.ReadResponseAsync(); // init response; no initialized notification sent

        await h.SendAsync(new JsonRpcRequest { Id = 2, Method = McpMethods.ToolsList });
        var response = await h.ReadResponseAsync();

        Assert.True(response.IsError);
        Assert.Equal(McpErrorCodes.InvalidRequest, response.Error!.Code);
    }

    [Fact]
    public async Task Ping_BeforeInitialized_IsAllowed()
    {
        await using var h = await Harness.StartAsync();
        await h.SendInitializeAsync();
        await h.ReadResponseAsync();

        await h.SendAsync(new JsonRpcRequest { Id = 2, Method = McpMethods.Ping });
        var response = await h.ReadResponseAsync();

        Assert.False(response.IsError);
    }

    [Fact]
    public async Task Operation_AfterInitialized_IsAccepted()
    {
        await using var h = await Harness.StartAsync(s =>
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok"))));
        await h.SendInitializeAsync();
        await h.ReadResponseAsync();
        await h.SendInitializedAsync();

        await h.SendAsync(new JsonRpcRequest { Id = 2, Method = McpMethods.ToolsList });
        var response = await h.ReadResponseAsync();

        Assert.False(response.IsError);
    }

    [Fact]
    public async Task DuplicateInitialize_IsRejected()
    {
        await using var h = await Harness.StartAsync();
        await h.SendInitializeAsync(id: 1);
        await h.ReadResponseAsync();

        await h.SendInitializeAsync(id: 2);
        var response = await h.ReadResponseAsync();

        Assert.True(response.IsError);
        Assert.Equal(McpErrorCodes.InvalidRequest, response.Error!.Code);
    }
}
