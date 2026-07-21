using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests the server-side bidirectional request engine (issue #42): the server can initiate
/// requests to the client and correlate the responses, including under overlapping request IDs.
/// </summary>
public class ServerInitiatedRequestTests
{
    private static (McpServer server, Task<McpClient> clientTask, CancellationTokenSource cts) Connect(
        McpClientOptions clientOptions, Action<McpServer>? configureServer = null)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new McpServer(serverTransport);
        configureServer?.Invoke(server);
        _ = server.RunAsync(cts.Token);

        var clientTask = McpClient.ConnectAsync(clientTransport, clientOptions, cancellationToken: cts.Token);
        return (server, clientTask, cts);
    }

    [Fact]
    public async Task Server_PingsClient_AndConsumesResponse()
    {
        var (server, clientTask, cts) = Connect(new McpClientOptions());
        await using var client = await clientTask;

        await server.PingClientAsync(cts.Token); // must not throw
        cts.Dispose();
    }

    [Fact]
    public async Task Server_ListsClientRoots()
    {
        var options = new McpClientOptions
        {
            RootProvider = new StaticRootProvider(
                new Root { Uri = "file:///a", Name = "a" },
                new Root { Uri = "file:///b", Name = "b" })
        };
        var (server, clientTask, cts) = Connect(options);
        await using var client = await clientTask;

        var roots = await server.ListRootsAsync(cts.Token);

        Assert.Equal(2, roots.Count);
        Assert.Contains(roots, r => r.Uri == "file:///a");
        cts.Dispose();
    }

    [Fact]
    public async Task Server_RequestsSampling_FromClientHandler()
    {
        var options = new McpClientOptions { SamplingHandler = new EchoSamplingHandler() };
        var (server, clientTask, cts) = Connect(options);
        await using var client = await clientTask;

        var result = await server.CreateMessageAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10
        }, cts.Token);

        Assert.Equal("echo-model", result.Model);
        cts.Dispose();
    }

    [Fact]
    public async Task Server_RequestsElicitation_FromClientHandler()
    {
        var options = new McpClientOptions { ElicitationHandler = new DeclineElicitationHandler() };
        var (server, clientTask, cts) = Connect(options);
        await using var client = await clientTask;

        var result = await server.ElicitAsync(new ElicitRequest { Message = "?" }, cts.Token);

        Assert.Equal("decline", result.Action);
        cts.Dispose();
    }

    [Fact]
    public async Task ListRoots_Throws_WhenClientLacksRootsCapability()
    {
        var (server, clientTask, cts) = Connect(new McpClientOptions()); // no RootProvider
        await using var client = await clientTask;

        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(
            () => server.ListRootsAsync(cts.Token));
        cts.Dispose();
    }

    [Fact]
    public async Task ConcurrentClientAndServerRequests_WithOverlappingIds_DoNotCrossCorrelate()
    {
        // Client will call server tools while the server pings/roots the client concurrently.
        var options = new McpClientOptions
        {
            RootProvider = new StaticRootProvider(new Root { Uri = "file:///r", Name = "r" })
        };
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new McpServer(serverTransport);
        server.AddTool("echo", "echo", (_, _) => Task.FromResult(CallToolResult.Text("tool-ok")));
        _ = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, options, cancellationToken: cts.Token);

        // Both directions in flight at once; each side's IDs start at 1 and overlap.
        var clientCall = client.CallToolAsync("echo", null, cts.Token);
        var serverRoots = server.ListRootsAsync(cts.Token);
        var serverPing = server.PingClientAsync(cts.Token);

        await Task.WhenAll(clientCall, serverRoots, serverPing);

        Assert.Equal("tool-ok", ((TextContent)(await clientCall).Content[0]).Text);
        Assert.Equal("file:///r", (await serverRoots)[0].Uri);
        cts.Dispose();
    }

    private sealed class EchoSamplingHandler : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct) =>
            Task.FromResult(new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = [new TextContent { Text = "echo" }],
                Model = "echo-model"
            });
    }

    private sealed class DeclineElicitationHandler : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) =>
            Task.FromResult(ElicitResult.Decline());
    }
}
