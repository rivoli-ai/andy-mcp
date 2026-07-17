using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;

namespace Andy.MCP.Tests.Client;

#region Roots Tests (#12)

public class RootsTests
{
    [Fact]
    public void StaticRootProvider_GetRoots()
    {
        var provider = new StaticRootProvider(
            new Root { Uri = "file:///home/user/project", Name = "Project" },
            new Root { Uri = "file:///home/user/data" });

        var roots = provider.GetRoots();
        Assert.Equal(2, roots.Count);
        Assert.Equal("file:///home/user/project", roots[0].Uri);
        Assert.Equal("Project", roots[0].Name);
        Assert.Equal("file:///home/user/data", roots[1].Uri);
        Assert.Null(roots[1].Name);
    }

    [Fact]
    public void StaticRootProvider_AddRoot_FiresEvent()
    {
        var provider = new StaticRootProvider();
        bool eventFired = false;
        provider.RootsChanged += (_, _) => eventFired = true;

        provider.AddRoot(new Root { Uri = "file:///new" });

        Assert.True(eventFired);
        Assert.Single(provider.GetRoots());
    }

    [Fact]
    public void StaticRootProvider_RemoveRoot_FiresEvent()
    {
        var provider = new StaticRootProvider(new Root { Uri = "file:///old" });
        bool eventFired = false;
        provider.RootsChanged += (_, _) => eventFired = true;

        Assert.True(provider.RemoveRoot("file:///old"));
        Assert.True(eventFired);
        Assert.Empty(provider.GetRoots());
    }

    [Fact]
    public void StaticRootProvider_RemoveNonexistent_ReturnsFalse()
    {
        var provider = new StaticRootProvider();
        bool eventFired = false;
        provider.RootsChanged += (_, _) => eventFired = true;

        Assert.False(provider.RemoveRoot("file:///nope"));
        Assert.False(eventFired);
    }

    [Fact]
    public void Root_Serializes()
    {
        var root = new Root { Uri = "file:///home/user", Name = "Home" };
        var json = JsonSerializer.Serialize(root, McpJsonDefaults.Options);

        Assert.Contains("\"uri\":\"file:///home/user\"", json);
        Assert.Contains("\"name\":\"Home\"", json);
    }

    [Fact]
    public void Root_WithoutName_OmitsField()
    {
        var root = new Root { Uri = "file:///path" };
        var json = JsonSerializer.Serialize(root, McpJsonDefaults.Options);

        Assert.DoesNotContain("name", json);
    }

    [Fact]
    public async Task Client_WithRootProvider_DeclaresCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        ClientCapabilities? receivedCaps = null;
        var serverTask = StartServerCapturing(st, caps => receivedCaps = caps, cts.Token);

        var provider = new StaticRootProvider(new Root { Uri = "file:///project" });
        await using var client = await McpClient.ConnectAsync(ct,
            new McpClientOptions { RootProvider = provider },
            cancellationToken: cts.Token);

        Assert.NotNull(receivedCaps?.Roots);
        Assert.True(receivedCaps!.Roots!.ListChanged);
    }

    [Fact]
    public async Task Server_RequestsRoots_ClientReturns()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var serverTask = StartServerThatRequestsRoots(st, cts.Token);

        var provider = new StaticRootProvider(
            new Root { Uri = "file:///a", Name = "A" },
            new Root { Uri = "file:///b", Name = "B" });

        await using var client = await McpClient.ConnectAsync(ct,
            new McpClientOptions { RootProvider = provider },
            cancellationToken: cts.Token);

        // Give time for server to request roots and receive response
        await Task.Delay(500);
    }

    private static Task StartServerCapturing(InMemoryServerTransport st,
        Action<ClientCapabilities> onCapabilities, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            await st.StartAsync(ct);
            await foreach (var msg in st.Messages.WithCancellation(ct))
            {
                if (msg is JsonRpcRequest { Method: McpMethods.Initialize } req)
                {
                    var p = req.GetParams<InitializeParams>()!;
                    onCapabilities(p.Capabilities);

                    var result = new InitializeResult
                    {
                        ProtocolVersion = McpSession.LatestProtocolVersion,
                        Capabilities = new ServerCapabilities(),
                        ServerInfo = new Implementation("Mock", "1.0")
                    };
                    await st.SendAsync(JsonRpcResponse.Success(req.Id, McpJsonDefaults.ToElement(result)), ct);
                }
            }
        }, ct);
    }

    private static Task StartServerThatRequestsRoots(InMemoryServerTransport st, CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            await st.StartAsync(ct);
            await foreach (var msg in st.Messages.WithCancellation(ct))
            {
                if (msg is JsonRpcRequest { Method: McpMethods.Initialize } req)
                {
                    var result = new InitializeResult
                    {
                        ProtocolVersion = McpSession.LatestProtocolVersion,
                        Capabilities = new ServerCapabilities(),
                        ServerInfo = new Implementation("Mock", "1.0")
                    };
                    await st.SendAsync(JsonRpcResponse.Success(req.Id, McpJsonDefaults.ToElement(result)), ct);
                }
                else if (msg is JsonRpcNotification { Method: McpMethods.NotificationsInitialized })
                {
                    // Now request roots from client
                    await st.SendAsync(new JsonRpcRequest
                    {
                        Id = 100,
                        Method = McpMethods.RootsList
                    }, ct);
                }
                else if (msg is JsonRpcResponse resp && resp.Id.AsNumber() == 100)
                {
                    // Verify roots response
                    var rootsResult = resp.GetResult<ListRootsResult>();
                    if (rootsResult?.Roots.Count == 2)
                        return; // Success
                }
            }
        }, ct);
    }
}

#endregion

#region Sampling Tests (#13)

public class SamplingTests
{
    [Fact]
    public void CreateMessageRequest_Serializes()
    {
        var req = new CreateMessageRequest
        {
            Messages = [new SamplingMessage
            {
                Role = Role.User,
                Content = [new TextContent { Text = "What is 2+2?" }]
            }],
            MaxTokens = 100,
            SystemPrompt = "You are a math helper.",
            ModelPreferences = new ModelPreferences
            {
                Hints = [new ModelHint { Name = "claude-3" }],
                IntelligencePriority = 0.8
            }
        };

        var json = JsonSerializer.Serialize(req, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<CreateMessageRequest>(json, McpJsonDefaults.Options)!;

        Assert.Single(deserialized.Messages);
        Assert.Equal(100, deserialized.MaxTokens);
        Assert.Equal("You are a math helper.", deserialized.SystemPrompt);
        Assert.Equal("claude-3", deserialized.ModelPreferences!.Hints![0].Name);
        Assert.Equal(0.8, deserialized.ModelPreferences.IntelligencePriority);
    }

    [Fact]
    public void CreateMessageResult_Serializes()
    {
        var result = new CreateMessageResult
        {
            Role = Role.Assistant,
            Content = [new TextContent { Text = "4" }],
            Model = "claude-sonnet-4-20250514",
            StopReason = "endTurn"
        };

        var json = JsonSerializer.Serialize(result, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<CreateMessageResult>(json, McpJsonDefaults.Options)!;

        Assert.Equal(Role.Assistant, deserialized.Role);
        Assert.Equal("claude-sonnet-4-20250514", deserialized.Model);
        Assert.Equal("endTurn", deserialized.StopReason);
    }

    [Fact]
    public async Task Client_WithSamplingHandler_DeclaresCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        ClientCapabilities? receivedCaps = null;
        var serverTask = Task.Run(async () =>
        {
            await st.StartAsync(cts.Token);
            await foreach (var msg in st.Messages.WithCancellation(cts.Token))
            {
                if (msg is JsonRpcRequest { Method: McpMethods.Initialize } req)
                {
                    receivedCaps = req.GetParams<InitializeParams>()!.Capabilities;
                    var result = new InitializeResult
                    {
                        ProtocolVersion = McpSession.LatestProtocolVersion,
                        Capabilities = new ServerCapabilities(),
                        ServerInfo = new Implementation("Mock", "1.0")
                    };
                    await st.SendAsync(JsonRpcResponse.Success(req.Id, McpJsonDefaults.ToElement(result)), cts.Token);
                }
            }
        }, cts.Token);

        await using var client = await McpClient.ConnectAsync(ct,
            new McpClientOptions
            {
                SamplingHandler = new MockSamplingHandler()
            },
            cancellationToken: cts.Token);

        Assert.NotNull(receivedCaps?.Sampling);
    }

    private sealed class MockSamplingHandler : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct)
        {
            return Task.FromResult(new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = [new TextContent { Text = "Mock response" }],
                Model = "mock-model",
                StopReason = "endTurn"
            });
        }
    }
}

#endregion

#region Elicitation Tests (#14)

public class ElicitationTests
{
    [Fact]
    public void ElicitResult_Accept()
    {
        var content = McpJsonDefaults.ToElement(new { name = "Alice", age = 30 });
        var result = ElicitResult.Accept(content);

        Assert.Equal("accept", result.Action);
        Assert.NotNull(result.Content);
    }

    [Fact]
    public void ElicitResult_Decline()
    {
        var result = ElicitResult.Decline();
        Assert.Equal("decline", result.Action);
        Assert.Null(result.Content);
    }

    [Fact]
    public void ElicitResult_Cancel()
    {
        var result = ElicitResult.Cancel();
        Assert.Equal("cancel", result.Action);
        Assert.Null(result.Content);
    }

    [Fact]
    public void ElicitRequest_Serializes()
    {
        var req = new ElicitRequest
        {
            Message = "Please enter your name",
            RequestedSchema = McpJsonDefaults.ToElement(new
            {
                type = "object",
                properties = new { name = new { type = "string" } },
                required = new[] { "name" }
            })
        };

        var json = JsonSerializer.Serialize(req, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ElicitRequest>(json, McpJsonDefaults.Options)!;

        Assert.Equal("Please enter your name", deserialized.Message);
        Assert.Equal("object", deserialized.RequestedSchema!.Value.GetProperty("type").GetString());
    }

    [Fact]
    public void ElicitResult_Accept_RoundTrips()
    {
        var content = McpJsonDefaults.ToElement(new { email = "alice@example.com" });
        var result = ElicitResult.Accept(content);

        var json = JsonSerializer.Serialize(result, McpJsonDefaults.Options);
        var deserialized = JsonSerializer.Deserialize<ElicitResult>(json, McpJsonDefaults.Options)!;

        Assert.Equal("accept", deserialized.Action);
        Assert.Equal("alice@example.com", deserialized.Content!.Value.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Client_WithElicitationHandler_DeclaresCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        ClientCapabilities? receivedCaps = null;
        var serverTask = Task.Run(async () =>
        {
            await st.StartAsync(cts.Token);
            await foreach (var msg in st.Messages.WithCancellation(cts.Token))
            {
                if (msg is JsonRpcRequest { Method: McpMethods.Initialize } req)
                {
                    receivedCaps = req.GetParams<InitializeParams>()!.Capabilities;
                    var result = new InitializeResult
                    {
                        ProtocolVersion = McpSession.LatestProtocolVersion,
                        Capabilities = new ServerCapabilities(),
                        ServerInfo = new Implementation("Mock", "1.0")
                    };
                    await st.SendAsync(JsonRpcResponse.Success(req.Id, McpJsonDefaults.ToElement(result)), cts.Token);
                }
            }
        }, cts.Token);

        await using var client = await McpClient.ConnectAsync(ct,
            new McpClientOptions
            {
                ElicitationHandler = new MockElicitationHandler()
            },
            cancellationToken: cts.Token);

        Assert.NotNull(receivedCaps?.Elicitation);
    }

    private sealed class MockElicitationHandler : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct)
        {
            return Task.FromResult(ElicitResult.Decline());
        }
    }
}

#endregion
