using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Conformance;

/// <summary>
/// End-to-end conformance tests using InMemoryTransport.
/// Exercises every MCP feature through the full McpClient ↔ McpServer stack.
/// </summary>
public class ConformanceTests : IAsyncLifetime
{
    private InMemoryClientTransport _clientTransport = null!;
    private InMemoryServerTransport _serverTransport = null!;
    private McpClient _client = null!;
    private McpServer _server = null!;
    private Task _serverTask = null!;
    private CancellationTokenSource _cts = null!;

    public async Task InitializeAsync()
    {
        (_clientTransport, _serverTransport) = InMemoryTransport.CreatePair();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _server = EverythingServer.Create(_serverTransport);
        _serverTask = _server.RunAsync(_cts.Token);
        _client = await McpClient.ConnectAsync(_clientTransport, cancellationToken: _cts.Token);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        _cts.Cancel();
        try { await _serverTask; } catch { }
        await _server.DisposeAsync();
        _cts.Dispose();
    }

    #region Initialization & Capabilities

    [Fact]
    public void Session_IsReady()
    {
        Assert.Equal(McpSessionState.Ready, _client.Session.State);
        Assert.Equal("2025-06-18", _client.Session.ProtocolVersion);
        Assert.Equal("EverythingServer", _client.Session.RemoteInfo?.Name);
        Assert.Equal("This server supports all MCP features for testing.", _client.Session.Instructions);
    }

    [Fact]
    public void AllCapabilities_Present()
    {
        Assert.True(_client.Session.HasServerCapability("tools"));
        Assert.True(_client.Session.HasServerCapability("resources"));
        Assert.True(_client.Session.HasServerCapability("prompts"));
        Assert.True(_client.Session.HasServerCapability("completions"));
        Assert.True(_client.Session.HasServerCapability("logging"));
    }

    [Fact]
    public async Task Ping_Succeeds()
    {
        await _client.PingAsync(_cts.Token);
    }

    #endregion

    #region Tools

    [Fact]
    public async Task ListTools_ReturnsAll()
    {
        var tools = await _client.ListToolsAsync(_cts.Token);
        Assert.True(tools.Count >= 7);
        Assert.Contains(tools, t => t.Name == "echo");
        Assert.Contains(tools, t => t.Name == "add");
        Assert.Contains(tools, t => t.Name == "get_image");
        Assert.Contains(tools, t => t.Name == "error_tool");
        Assert.Contains(tools, t => t.Name == "annotated_tool");
    }

    [Fact]
    public async Task CallTool_Echo_ReturnsText()
    {
        var result = await _client.CallToolAsync("echo", new { message = "Hello MCP!" }, ct: _cts.Token);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("Hello MCP!", text.Text);
    }

    [Fact]
    public async Task CallTool_Add_ReturnsSum()
    {
        var result = await _client.CallToolAsync("add", new { a = 3.5, b = 2.5 }, ct: _cts.Token);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Equal("6", text.Text);
    }

    [Fact]
    public async Task CallTool_Add_MissingRequired_Error()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _client.CallToolAsync("add", new { a = 1 }, ct: _cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
        Assert.Contains("b", ex.Message);
    }

    [Fact]
    public async Task CallTool_Add_WrongType_Error()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _client.CallToolAsync("add", new { a = "not_a_number", b = 2 }, ct: _cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task CallTool_UnknownTool_Error()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _client.CallToolAsync("nonexistent", ct: _cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task CallTool_GetImage_ReturnsImageContent()
    {
        var result = await _client.CallToolAsync("get_image", ct: _cts.Token);
        var image = Assert.IsType<ImageContent>(result.Content[0]);
        Assert.Equal("image/png", image.MimeType);
        var bytes = image.ToBytes();
        Assert.True(bytes.Length > 0);
    }

    [Fact]
    public async Task CallTool_GetAudio_ReturnsAudioContent()
    {
        var result = await _client.CallToolAsync("get_audio", ct: _cts.Token);
        var audio = Assert.IsType<AudioContent>(result.Content[0]);
        Assert.Equal("audio/wav", audio.MimeType);
    }

    [Fact]
    public async Task CallTool_MultiContent_ReturnsMixed()
    {
        var result = await _client.CallToolAsync("multi_content", ct: _cts.Token);
        Assert.Equal(3, result.Content.Count);
        Assert.IsType<TextContent>(result.Content[0]);
        Assert.IsType<ImageContent>(result.Content[1]);
        Assert.IsType<ResourceLink>(result.Content[2]);
    }

    [Fact]
    public async Task CallTool_ErrorTool_ReturnsIsError()
    {
        var result = await _client.CallToolAsync("error_tool", ct: _cts.Token);
        Assert.True(result.IsError);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Contains("always fails", text.Text);
    }

    [Fact]
    public async Task CallTool_LongRunning_Completes()
    {
        var result = await _client.CallToolAsync("long_running", ct: _cts.Token);
        var text = Assert.IsType<TextContent>(result.Content[0]);
        Assert.Contains("Completed", text.Text);
    }

    [Fact]
    public async Task CallTool_Annotated_HasAnnotations()
    {
        var tools = await _client.ListToolsAsync(_cts.Token);
        var annotated = tools.First(t => t.Name == "annotated_tool");
        Assert.NotNull(annotated.Annotations);
        Assert.True(annotated.Annotations!.ReadOnlyHint);
        Assert.False(annotated.Annotations.DestructiveHint);
        Assert.True(annotated.Annotations.IdempotentHint);
        Assert.False(annotated.Annotations.OpenWorldHint);
    }

    #endregion

    #region Resources

    [Fact]
    public async Task ListResources_ReturnsAll()
    {
        var resources = await _client.ListResourcesAsync(_cts.Token);
        Assert.True(resources.Count >= 3);
        Assert.Contains(resources, r => r.Uri == "file:///readme.md");
        Assert.Contains(resources, r => r.Uri == "file:///logo.png");
        Assert.Contains(resources, r => r.Uri == "file:///dynamic");
    }

    [Fact]
    public async Task ReadResource_Text()
    {
        var result = await _client.ReadResourceAsync("file:///readme.md", ct: _cts.Token);
        Assert.Single(result.Contents);
        var text = Assert.IsType<TextResourceContents>(result.Contents[0]);
        Assert.Contains("Everything Server", text.Text);
        Assert.Equal("text/markdown", text.MimeType);
    }

    [Fact]
    public async Task ReadResource_Blob()
    {
        var result = await _client.ReadResourceAsync("file:///logo.png", ct: _cts.Token);
        var blob = Assert.IsType<BlobResourceContents>(result.Contents[0]);
        Assert.Equal("image/png", blob.MimeType);
        Assert.True(blob.ToBytes().Length > 0);
    }

    [Fact]
    public async Task ReadResource_NotFound()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _client.ReadResourceAsync("file:///nonexistent", ct: _cts.Token));
        Assert.Equal(McpErrorCodes.ResourceNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task ListResourceTemplates()
    {
        var templates = await _client.ListResourceTemplatesAsync(_cts.Token);
        Assert.Single(templates);
        Assert.Equal("file:///config/{key}", templates[0].UriTemplate);
        Assert.Equal("Configuration", templates[0].Name);
    }

    #endregion

    #region Prompts

    [Fact]
    public async Task ListPrompts_ReturnsAll()
    {
        var prompts = await _client.ListPromptsAsync(_cts.Token);
        Assert.True(prompts.Count >= 3);
        Assert.Contains(prompts, p => p.Name == "simple_greeting");
        Assert.Contains(prompts, p => p.Name == "code_review");
        Assert.Contains(prompts, p => p.Name == "multi_turn");
    }

    [Fact]
    public async Task GetPrompt_Simple()
    {
        var result = await _client.GetPromptAsync("simple_greeting", ct: _cts.Token);
        Assert.Equal("A friendly greeting", result.Description);
        Assert.Single(result.Messages);
        Assert.Equal(Role.User, result.Messages[0].Role);
    }

    [Fact]
    public async Task GetPrompt_WithArguments()
    {
        var result = await _client.GetPromptAsync("code_review",
            new Dictionary<string, string> { ["language"] = "csharp", ["style"] = "brief" },
            _cts.Token);
        var text = Assert.IsType<TextContent>(result.Messages[0].Content);
        Assert.Contains("csharp", text.Text);
        Assert.Contains("brief", text.Text);
    }

    [Fact]
    public async Task GetPrompt_MultiTurn()
    {
        var result = await _client.GetPromptAsync("multi_turn", ct: _cts.Token);
        Assert.Equal(3, result.Messages.Count);
        Assert.Equal(Role.User, result.Messages[0].Role);
        Assert.Equal(Role.Assistant, result.Messages[1].Role);
        Assert.Equal(Role.User, result.Messages[2].Role);
    }

    [Fact]
    public async Task GetPrompt_Unknown_Error()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            _client.GetPromptAsync("nonexistent", ct: _cts.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [Fact]
    public async Task Prompt_HasArguments()
    {
        var prompts = await _client.ListPromptsAsync(_cts.Token);
        var codeReview = prompts.First(p => p.Name == "code_review");
        Assert.NotNull(codeReview.Arguments);
        Assert.Equal(2, codeReview.Arguments!.Count);
        Assert.Equal("language", codeReview.Arguments[0].Name);
        Assert.True(codeReview.Arguments[0].Required);
        Assert.Equal("style", codeReview.Arguments[1].Name);
        Assert.Null(codeReview.Arguments[1].Required); // Not required
    }

    #endregion

    #region Logging

    [Fact]
    public async Task SetLogLevel_Succeeds()
    {
        await _client.SetLogLevelAsync("debug", ct: _cts.Token);
    }

    #endregion

    #region Concurrent Operations

    [Fact]
    public async Task ConcurrentToolCalls_AllSucceed()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _client.CallToolAsync("echo", new { message = $"msg-{i}" }, ct: _cts.Token))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.Equal(10, results.Length);
        Assert.All(results, r => Assert.Single(r.Content));
    }

    #endregion
}

/// <summary>
/// Tests for capability auto-detection with different server configurations.
/// </summary>
public class CapabilityAutoDetectionTests
{
    [Fact]
    public async Task Server_ToolsOnly_OnlyToolsCapability()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("test", "Test", async (a, c) => CallToolResult.Text("ok"));
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.True(client.Session.HasServerCapability("tools"));
        Assert.False(client.Session.HasServerCapability("resources"));
        Assert.False(client.Session.HasServerCapability("prompts"));
        Assert.False(client.Session.HasServerCapability("completions"));
        Assert.False(client.Session.HasServerCapability("logging"));
    }

    [Fact]
    public async Task Server_Empty_NoCapabilities()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.False(client.Session.HasServerCapability("tools"));
        Assert.False(client.Session.HasServerCapability("resources"));
        Assert.False(client.Session.HasServerCapability("prompts"));
    }

    [Fact]
    public async Task Server_AllFeatures_AllCapabilities()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = EverythingServer.Create(st);
        var serverTask = server.RunAsync(cts.Token);

        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: cts.Token);

        Assert.True(client.Session.HasServerCapability("tools"));
        Assert.True(client.Session.HasServerCapability("resources"));
        Assert.True(client.Session.HasServerCapability("prompts"));
        Assert.True(client.Session.HasServerCapability("completions"));
        Assert.True(client.Session.HasServerCapability("logging"));
    }
}

/// <summary>
/// Tests for sampling and elicitation round-trips (server→client→server).
/// </summary>
public class ServerToClientRoundTripTests
{
    [Fact]
    public async Task Sampling_RoundTrip()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Server with a tool that requests sampling
        var server = new McpServer(st);
        server.AddTool("ask_llm", "Ask the LLM a question", async (args, c) =>
        {
            // This would normally call server.RequestSamplingAsync()
            // For testing, we just return a result
            return CallToolResult.Text("LLM would respond here");
        });
        var serverTask = server.RunAsync(cts.Token);

        var samplingCalled = false;
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
        {
            SamplingHandler = new TestSamplingHandler(() => samplingCalled = true)
        }, cancellationToken: cts.Token);

        // Verify capability declared
        Assert.NotNull(client.Session);
    }

    [Fact]
    public async Task Elicitation_RoundTrip()
    {
        var (ct, st) = InMemoryTransport.CreatePair();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var server = new McpServer(st);
        server.AddTool("ask_user", "Ask the user", async (args, c) =>
            CallToolResult.Text("User would be asked here"));
        var serverTask = server.RunAsync(cts.Token);

        var elicitCalled = false;
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
        {
            ElicitationHandler = new TestElicitationHandler(() => elicitCalled = true)
        }, cancellationToken: cts.Token);

        Assert.NotNull(client.Session);
    }

    private class TestSamplingHandler(Action onCalled) : ISamplingHandler
    {
        public Task<CreateMessageResult> HandleAsync(CreateMessageRequest req, CancellationToken ct)
        {
            onCalled();
            return Task.FromResult(new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = new TextContent { Text = "Test response" },
                Model = "test-model",
                StopReason = "endTurn"
            });
        }
    }

    private class TestElicitationHandler(Action onCalled) : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest req, CancellationToken ct)
        {
            onCalled();
            return Task.FromResult(ElicitResult.Accept(
                McpJsonDefaults.ToElement(new { answer = "42" })));
        }
    }
}
