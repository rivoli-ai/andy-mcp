using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class McpServerTests
{
    [Fact]
    public async Task Server_InitializesWithClient()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport, new McpServerOptions
        {
            ServerInfo = new Implementation("TestServer", "1.0.0"),
            Instructions = "Test instructions"
        });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        Assert.Equal(McpSessionState.Ready, client.Session.State);
        Assert.Equal("TestServer", client.Session.RemoteInfo?.Name);
        Assert.Equal("Test instructions", client.Session.Instructions);
    }

    [Fact]
    public async Task Server_Ping()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        await client.PingAsync(cts.Token); // Should not throw
    }

    [Fact]
    public async Task Server_ListTools_ReturnsRegistered()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddTool("greet", "Say hello", async (args, ct) =>
            CallToolResult.Text("Hello!"));
        server.AddTool("add", "Add numbers", async (args, ct) =>
            CallToolResult.Text("42"));

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var tools = await client.ListToolsAsync(cts.Token);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t.Name == "greet");
        Assert.Contains(tools, t => t.Name == "add");
    }

    [Fact]
    public async Task Server_CallTool_ExecutesHandler()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddTool("greet", "Say hello", async (args, ct) =>
        {
            var name = args?.GetProperty("name").GetString() ?? "World";
            return CallToolResult.Text($"Hello, {name}!");
        });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var result = await client.CallToolAsync("greet", new { name = "Alice" }, cts.Token);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Hello, Alice!", text.Text);
    }

    [Fact]
    public async Task Server_CallUnknownTool_ReturnsError()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        // Server has no tools capability since none registered
        // The client will throw McpCapabilityNotAvailableException
        Assert.Throws<McpCapabilityNotAvailableException>(() =>
            client.CallToolAsync("nonexistent", ct: cts.Token).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Server_ToolError_ReturnsIsError()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddTool("fail", "Always fails", async (args, ct) =>
        {
            throw new InvalidOperationException("Something went wrong");
        });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var result = await client.CallToolAsync("fail", ct: cts.Token);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Contains("Something went wrong", text.Text);
    }

    [Fact]
    public async Task Server_ListResources()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddResource("file:///config.json", "Configuration", async (uri, ct) =>
            new TextResourceContents { Uri = uri, Text = "{}", MimeType = "application/json" });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var resources = await client.ListResourcesAsync(cts.Token);
        Assert.Single(resources);
        Assert.Equal("file:///config.json", resources[0].Uri);
    }

    [Fact]
    public async Task Server_ReadResource()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddResource("file:///readme.md", "README", async (uri, ct) =>
            new TextResourceContents { Uri = uri, Text = "# Hello", MimeType = "text/markdown" });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var result = await client.ReadResourceAsync("file:///readme.md", cts.Token);
        Assert.Single(result.Contents);
        var text = Assert.IsType<TextResourceContents>(result.Contents[0]);
        Assert.Equal("# Hello", text.Text);
    }

    [Fact]
    public async Task Server_ReadUnknownResource_ReturnsError()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddResource("file:///exists", "R", async (uri, ct) =>
            new TextResourceContents { Uri = uri, Text = "ok" });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            client.ReadResourceAsync("file:///not-here", cts.Token));
        Assert.Equal(McpErrorCodes.ResourceNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Server_ListPrompts()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddPrompt("review", "Code review", async (name, args, ct) =>
            new GetPromptResult
            {
                Description = "Code review prompt",
                Messages = [new PromptMessage { Role = Role.User, Content = new TextContent { Text = "Review this code" } }]
            });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var prompts = await client.ListPromptsAsync(cts.Token);
        Assert.Single(prompts);
        Assert.Equal("review", prompts[0].Name);
    }

    [Fact]
    public async Task Server_GetPrompt()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        server.AddPrompt("greet", "Greeting prompt", async (name, args, ct) =>
            new GetPromptResult
            {
                Messages = [new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContent { Text = $"Hello {args?["name"] ?? "World"}!" }
                }]
            });

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        var result = await client.GetPromptAsync("greet",
            new Dictionary<string, string> { ["name"] = "Alice" }, cts.Token);
        var text = Assert.IsType<TextContent>(result.Messages[0].Content);
        Assert.Equal("Hello Alice!", text.Text);
    }

    [Fact]
    public async Task Server_CapabilitiesAutoDetected()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Server with tools only
        var server = new McpServer(serverTransport);
        server.AddTool("test", "Test tool", async (_, ct) => CallToolResult.Text("ok"));

        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        Assert.True(client.Session.HasServerCapability("tools"));
        Assert.False(client.Session.HasServerCapability("resources"));
        Assert.False(client.Session.HasServerCapability("prompts"));
    }

    [Fact]
    public async Task Server_NoRegistrations_NoCapabilities()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        var serverTask = server.RunAsync(cts.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: cts.Token);

        Assert.False(client.Session.HasServerCapability("tools"));
        Assert.False(client.Session.HasServerCapability("resources"));
        Assert.False(client.Session.HasServerCapability("prompts"));
    }

    [Fact]
    public async Task Server_UnknownMethod_ReturnsMethodNotFound()
    {
        // Test directly with server transport (no client) to check raw response
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var server = new McpServer(serverTransport);
        var serverTask = server.RunAsync(cts.Token);

        // Send init manually
        await clientTransport.ConnectAsync(cts.Token);
        var initReq = new JsonRpcRequest
        {
            Id = 1,
            Method = McpMethods.Initialize,
            Params = McpJsonDefaults.ToElement(new InitializeParams
            {
                ProtocolVersion = McpSession.LatestProtocolVersion,
                Capabilities = new ClientCapabilities(),
                ClientInfo = new Implementation("Test", "1.0")
            })
        };
        await clientTransport.SendAsync(initReq, cts.Token);

        // Read init response
        await foreach (var msg in clientTransport.Messages.WithCancellation(cts.Token))
        {
            if (msg is JsonRpcResponse) break;
        }

        // Send unknown method
        var unknownReq = new JsonRpcRequest { Id = 2, Method = "some/unknown/method" };
        await clientTransport.SendAsync(unknownReq, cts.Token);

        // Read response
        await foreach (var msg in clientTransport.Messages.WithCancellation(cts.Token))
        {
            if (msg is JsonRpcResponse resp)
            {
                Assert.True(resp.IsError);
                Assert.Equal(McpErrorCodes.MethodNotFound, resp.Error!.Code);
                return;
            }
        }

        Assert.Fail("No response received");
    }
}
