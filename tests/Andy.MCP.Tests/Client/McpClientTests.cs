using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Client;

public class McpClientTests
{
    /// <summary>
    /// Starts a mock server loop that handles initialize and subsequent requests.
    /// </summary>
    private static Task StartMockServer(
        InMemoryServerTransport server,
        ServerCapabilities? capabilities = null,
        Func<JsonRpcRequest, JsonRpcResponse?>? requestHandler = null,
        CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            await server.StartAsync(ct);
            await foreach (var msg in server.Messages.WithCancellation(ct))
            {
                if (msg is JsonRpcRequest request)
                {
                    if (request.Method == McpMethods.Initialize)
                    {
                        var result = new InitializeResult
                        {
                            ProtocolVersion = McpSession.LatestProtocolVersion,
                            Capabilities = capabilities ?? new ServerCapabilities
                            {
                                Tools = new ListChangedCapability { ListChanged = true },
                                Resources = new ResourcesCapability { Subscribe = true, ListChanged = true },
                                Prompts = new ListChangedCapability { ListChanged = true },
                                Logging = new EmptyCapability(),
                            },
                            ServerInfo = new Implementation("MockServer", "1.0.0")
                        };
                        await server.SendAsync(JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result)), ct);
                    }
                    else if (request.Method == McpMethods.Ping)
                    {
                        await server.SendAsync(JsonRpcResponse.Success(request.Id), ct);
                    }
                    else if (requestHandler is not null)
                    {
                        var response = requestHandler(request);
                        if (response is not null)
                            await server.SendAsync(response, ct);
                    }
                    else
                    {
                        await server.SendAsync(JsonRpcResponse.Failure(request.Id,
                            JsonRpcError.MethodNotFound($"Unknown method: {request.Method}")), ct);
                    }
                }
                // Ignore notifications (like initialized)
            }
        }, ct);
    }

    [Fact]
    public async Task ConnectAsync_InitializesSession()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport, ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        Assert.Equal(McpSessionState.Ready, client.Session.State);
        Assert.Equal(McpSession.LatestProtocolVersion, client.Session.ProtocolVersion);
        Assert.Equal("MockServer", client.Session.RemoteInfo?.Name);
    }

    [Fact]
    public async Task PingAsync_Succeeds()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport, ct: cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        await client.PingAsync(cts.Token); // Should not throw
    }

    [Fact]
    public async Task ListToolsAsync_ReturnsList()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport,
            requestHandler: request =>
            {
                if (request.Method == McpMethods.ToolsList)
                {
                    var result = new ToolsListResult
                    {
                        Tools = [
                            new Tool
                            {
                                Name = "get_weather",
                                Description = "Get weather for a city",
                                InputSchema = McpJsonDefaults.ToElement(new { type = "object", properties = new { city = new { type = "string" } } })
                            }
                        ]
                    };
                    return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
                }
                return null;
            },
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);
        var tools = await client.ListToolsAsync(cts.Token);

        Assert.Single(tools);
        Assert.Equal("get_weather", tools[0].Name);
    }

    [Fact]
    public async Task CallToolAsync_ReturnsResult()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport,
            requestHandler: request =>
            {
                if (request.Method == McpMethods.ToolsCall)
                {
                    var result = CallToolResult.Text("Sunny, 22°C");
                    return JsonRpcResponse.Success(request.Id, McpJsonDefaults.ToElement(result));
                }
                return null;
            },
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);
        var result = await client.CallToolAsync("get_weather", new { city = "Paris" }, cts.Token);

        Assert.Single(result.Content);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Sunny, 22°C", text.Text);
    }

    [Fact]
    public async Task CallToolAsync_ServerError_ThrowsMcpException()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport,
            requestHandler: request =>
            {
                if (request.Method == McpMethods.ToolsCall)
                {
                    return JsonRpcResponse.Failure(request.Id,
                        JsonRpcError.InvalidParams("Unknown tool: 'nonexistent'"));
                }
                return null;
            },
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.CallToolAsync("nonexistent", ct: cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
        Assert.Contains("nonexistent", ex.Message);
    }

    [Fact]
    public async Task CapabilityGating_ThrowsWhenNotAvailable()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Server without prompts capability
        var serverTask = StartMockServer(serverTransport,
            capabilities: new ServerCapabilities { Tools = new ListChangedCapability() },
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() =>
            client.ListPromptsAsync(cts.Token));
    }

    [Fact]
    public async Task ToolsChanged_EventFired()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartMockServer(serverTransport, ct: cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var eventFired = new TaskCompletionSource();
        client.ToolsChanged += (_, _) => eventFired.TrySetResult();

        // Server sends notification
        await serverTransport.SendAsync(new JsonRpcNotification
        {
            Method = McpMethods.NotificationsToolsListChanged
        });

        await Task.WhenAny(eventFired.Task, Task.Delay(2000));
        Assert.True(eventFired.Task.IsCompleted);
    }

    [Fact]
    public async Task Disconnect_CancelsPendingRequests()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Server that handles init but ignores tool calls (never responds)
        var serverTask = StartMockServer(serverTransport,
            requestHandler: request => null, // Don't respond
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        // Start a tool call that will never get a response
        var callTask = client.CallToolAsync("slow_tool", ct: cts.Token);

        // Simulate disconnect
        clientTransport.SimulateDisconnect();

        // The call should be cancelled
        await Assert.ThrowsAnyAsync<Exception>(() => callTask);
    }

    [Fact]
    public async Task ConcurrentToolCalls_AllComplete()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var serverTask = StartMockServer(serverTransport,
            requestHandler: request =>
            {
                if (request.Method == McpMethods.ToolsCall)
                {
                    var name = request.Params?.GetProperty("name").GetString() ?? "?";
                    return JsonRpcResponse.Success(request.Id,
                        McpJsonDefaults.ToElement(CallToolResult.Text($"Result for {name}")));
                }
                return null;
            },
            ct: cts.Token);

        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var tasks = Enumerable.Range(0, 10)
            .Select(i => client.CallToolAsync($"tool_{i}", ct: cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.Single(r.Content));
    }
}
