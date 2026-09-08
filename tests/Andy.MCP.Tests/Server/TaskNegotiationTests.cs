using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class TaskNegotiationTests
{
    private static async Task<JsonRpcResponse> Exchange(InMemoryClientTransport transport,
        IAsyncEnumerator<JsonRpcMessage> messages, string method, object parameters, int id, CancellationToken ct)
    {
        await transport.SendAsync(new JsonRpcRequest { Id = id, Method = method, Params = McpJsonDefaults.ToElement(parameters) }, ct);
        Assert.True(await messages.MoveNextAsync());
        return Assert.IsType<JsonRpcResponse>(messages.Current);
    }

    [Theory]
    [InlineData(true, "2025-11-25", "forbidden", false)]
    [InlineData(false, "2025-11-25", "optional", false)]
    [InlineData(true, "2025-06-18", "optional", false)]
    [InlineData(true, "2025-11-25", "optional", true)]
    public async Task UnsupportedAugmentation_IsIgnoredByReceiver(bool enabled, string revision, string support, bool createsTask)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st, new McpServerOptions { EnableExperimentalTasks = enabled });
        server.AddTool(new Tool { Name = "tool", InputSchema = McpJsonDefaults.ToElement(new { type = "object" }), Execution = new ToolExecution { TaskSupport = support } },
            (_, _) => Task.FromResult(CallToolResult.Text("ordinary")));
        var run = server.RunAsync(timeout.Token);
        await ct.ConnectAsync(timeout.Token);
        await using var messages = ct.Messages.GetAsyncEnumerator(timeout.Token);
        var initialized = await Exchange(ct, messages, McpMethods.Initialize, new InitializeParams
        {
            ProtocolVersion = revision,
            Capabilities = new ClientCapabilities(),
            ClientInfo = new Implementation("raw", "1")
        }, 1, timeout.Token);
        Assert.False(initialized.IsError);
        await ct.SendAsync(new JsonRpcNotification { Method = McpMethods.NotificationsInitialized }, timeout.Token);
        var response = await Exchange(ct, messages, McpMethods.ToolsCall, new { name = "tool", task = new { ttl = 60_000 } }, 2, timeout.Token);
        Assert.False(response.IsError);
        Assert.Equal(createsTask, response.Result!.Value.TryGetProperty("task", out _));
        if (!createsTask) Assert.Equal("ordinary", response.Result.Value.GetProperty("content")[0].GetProperty("text").GetString());
        await ct.DisposeAsync();
    }

    [Fact]
    public async Task ClientGatesDisabledTaskOperations_WhileOrdinaryToolsStillWork()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st, new McpServerOptions { EnableExperimentalTasks = false });
        server.AddTool("tool", "tool", (_, _) => Task.FromResult(CallToolResult.Text("ok")));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
        Assert.Null(client.Session.ServerCapabilities!.Tasks);
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.CallToolAsTaskAsync("tool", ct: timeout.Token));
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.ListTasksAsync(timeout.Token));
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.GetTaskAsync("id", timeout.Token));
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.CancelTaskAsync("id", timeout.Token));
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => client.GetTaskResultAsync("id", timeout.Token));
        Assert.Equal("ok", (await client.CallToolAsync("tool", ct: timeout.Token)).Content.OfType<TextContent>().Single().Text);
    }

    [Fact]
    public async Task ServerTaskPages_AutoPaginate_AndRejectInvalidCursors()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new InMemoryTaskStore();
        for (var i = 0; i < 5; i++) store.Create(null, "owner");
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st, new McpServerOptions { TaskStore = store, TaskOwnerKey = "owner", PageSize = 2 });
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
        var page = await client.ListTasksPageAsync(ct: timeout.Token);
        Assert.Equal(2, page.Tasks.Count);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(5, (await client.ListTasksAsync(timeout.Token)).Select(t => t.TaskId).Distinct().Count());
        var error = await Assert.ThrowsAsync<McpException>(() => client.ListTasksPageAsync(new PaginatedRequest { Cursor = "invalid" }, ct: timeout.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task ClientTaskPages_AutoPaginate_AndRejectInvalidCursors()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new InMemoryTaskStore();
        for (var i = 0; i < 51; i++) store.Create(null, "owner");
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions { TaskStore = store, TaskOwnerKey = "owner" }, cancellationToken: timeout.Token);
        var page = await server.ListClientTasksPageAsync(cancellationToken: timeout.Token);
        Assert.Equal(50, page.Tasks.Count);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(51, (await server.ListClientTasksAsync(timeout.Token)).Select(t => t.TaskId).Distinct().Count());
        var error = await Assert.ThrowsAsync<McpException>(() => server.ListClientTasksPageAsync(new PaginatedRequest { Cursor = "invalid" }, cancellationToken: timeout.Token));
        Assert.Equal(McpErrorCodes.InvalidParams, error.ErrorCode);
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.ElicitAsTaskAsync(ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() }), cancellationToken: timeout.Token));
    }

    [Fact]
    public async Task InvalidTaskToolArguments_RetainOrdinaryToolError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        server.AddTool("input", "input", McpJsonDefaults.ToElement(new { type = "object", required = new[] { "name" } }),
            (_, _) => throw new InvalidOperationException("must not execute"));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
        var ordinary = await client.CallToolAsync("input", new { }, timeout.Token);
        var created = await client.CallToolAsTaskAsync("input", new { }, ct: timeout.Token);
        var result = (await client.GetTaskResultAsync(created.Task.TaskId, timeout.Token)).Deserialize<CallToolResult>(McpJsonDefaults.Options)!;
        Assert.True(result.IsError);
        Assert.Equal(ordinary.Content.OfType<TextContent>().Single().Text, result.Content.OfType<TextContent>().Single().Text);
        Assert.Equal(McpTaskStatus.Failed, (await client.GetTaskAsync(created.Task.TaskId, timeout.Token)).Status);
    }
    private sealed class AcceptInput : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) => Task.FromResult(ElicitResult.Accept(McpJsonDefaults.ToElement(new { })));
    }

    [Fact]
    public async Task ClientSubCapabilities_AreHonoredIndependently()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
        {
            ElicitationHandler = new AcceptInput(),
            Capabilities = new ClientCapabilities { Tasks = new ClientTasksCapability { Requests = new ClientTaskRequests { Elicitation = new ElicitationTaskRequests { Create = new() } } } }
        }, cancellationToken: timeout.Token);
        var created = await server.ElicitAsTaskAsync(ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() }), cancellationToken: timeout.Token);
        await server.GetClientTaskResultAsync(created.Task.TaskId, timeout.Token);
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.ListClientTasksPageAsync(cancellationToken: timeout.Token));
        await Assert.ThrowsAsync<McpCapabilityNotAvailableException>(() => server.CancelClientTaskAsync(created.Task.TaskId, timeout.Token));
        Assert.Equal(McpTaskStatus.Completed, (await server.GetClientTaskAsync(created.Task.TaskId, timeout.Token)).Status);
    }

    [Fact]
    public async Task ClientWithoutTaskReception_IgnoresElicitationAugmentation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await st.StartAsync(timeout.Token);
        await using var messages = st.Messages.GetAsyncEnumerator(timeout.Token);
        var connecting = McpClient.ConnectAsync(ct, new McpClientOptions { ElicitationHandler = new AcceptInput(), EnableExperimentalTasks = false }, cancellationToken: timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var initialize = Assert.IsType<JsonRpcRequest>(messages.Current);
        Assert.Null(initialize.GetParams<InitializeParams>()!.Capabilities.Tasks);
        await st.SendAsync(JsonRpcResponse.Success(initialize.Id, McpJsonDefaults.ToElement(new InitializeResult
        {
            ProtocolVersion = "2025-11-25",
            Capabilities = new ServerCapabilities(),
            ServerInfo = new Implementation("raw", "1")
        })), timeout.Token);
        await using var client = await connecting;
        Assert.True(await messages.MoveNextAsync()); // initialized notification
        var request = ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() });
        var parameters = JsonSerializer.SerializeToNode(request, McpJsonDefaults.Options)!.AsObject();
        parameters["task"] = new System.Text.Json.Nodes.JsonObject { ["ttl"] = 60000 };
        await st.SendAsync(new JsonRpcRequest { Id = 10, Method = McpMethods.ElicitationCreate, Params = JsonSerializer.SerializeToElement(parameters) }, timeout.Token);
        Assert.True(await messages.MoveNextAsync());
        var response = Assert.IsType<JsonRpcResponse>(messages.Current);
        Assert.False(response.IsError);
        Assert.Equal("accept", response.Result!.Value.GetProperty("action").GetString());
        Assert.False(response.Result.Value.TryGetProperty("task", out _));
    }

}
