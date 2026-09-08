using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class TaskOutcomeTests
{
    [Fact]
    public async Task RpcError_RetainsCodeDataExtensionsAndOwnership()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "owner");
        using (var document = JsonDocument.Parse("""{"retry":17}"""))
        {
            Assert.True(store.SetError(task.TaskId, new JsonRpcError
            {
                Code = -32099,
                Message = "original",
                Data = document.RootElement,
                ExtensionData = new() { ["vendor"] = document.RootElement }
            }));
        }
        Assert.Null(store.GetError(task.TaskId, "other"));
        Assert.False(store.SetFailed(task.TaskId, "late"));
        Assert.False(store.SetResult(task.TaskId, McpJsonDefaults.ToElement(new { })));
        var response = await TaskResults.WaitAsync(store, "owner", new JsonRpcRequest
        {
            Id = 1,
            Method = McpMethods.TasksResult,
            Params = McpJsonDefaults.ToElement(new { taskId = task.TaskId })
        }, default);
        Assert.Equal(-32099, response.Error!.Code);
        Assert.Equal("original", response.Error.Message);
        Assert.Equal(17, response.Error.Data!.Value.GetProperty("retry").GetInt32());
        Assert.Equal(17, response.Error.ExtensionData!["vendor"].GetProperty("retry").GetInt32());
        Assert.Equal(task.TaskId, response.Error.ExtensionData["_meta"].GetProperty("io.modelcontextprotocol/related-task").GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task ThrownToolError_MatchesOrdinaryCallResult()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        server.AddTool("fail", "fail", (_, _) => throw new InvalidOperationException("original failure"));
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, cancellationToken: timeout.Token);
        var ordinary = await client.CallToolAsync("fail", ct: timeout.Token);
        var task = await client.CallToolAsTaskAsync("fail", ct: timeout.Token);
        var retained = (await client.GetTaskResultAsync(task.Task.TaskId, timeout.Token)).Deserialize<CallToolResult>(McpJsonDefaults.Options)!;
        Assert.Equal(ordinary.IsError, retained.IsError);
        Assert.Equal(ordinary.Content.OfType<TextContent>().Single().Text, retained.Content.OfType<TextContent>().Single().Text);
        Assert.Equal(McpTaskStatus.Failed, (await client.GetTaskAsync(task.Task.TaskId, timeout.Token)).Status);
    }

    private sealed class InvalidInputHandler : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) => throw new ArgumentException("original failure");
    }

    [Fact]
    public async Task ClientTaskError_MatchesOrdinaryRpcError_AndUsesInjectedStore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new InMemoryTaskStore();
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
        {
            ElicitationHandler = new InvalidInputHandler(),
            TaskStore = store,
            TaskOwnerKey = "trusted-owner"
        }, cancellationToken: timeout.Token);
        var request = ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() });
        var ordinary = await Assert.ThrowsAsync<McpException>(() => server.ElicitAsync(request, timeout.Token));
        var task = await server.ElicitAsTaskAsync(request, cancellationToken: timeout.Token);
        var retained = await Assert.ThrowsAsync<McpException>(() => server.GetClientTaskResultAsync(task.Task.TaskId, timeout.Token));
        Assert.Equal(ordinary.ErrorCode, retained.ErrorCode);
        Assert.Equal(ordinary.Message, retained.Message);
        Assert.NotNull(store.GetError(task.Task.TaskId, "trusted-owner"));
        Assert.Null(store.Get(task.Task.TaskId, null));
    }

    [Fact]
    public async Task DefaultOwners_IsolateServersSharingAStore()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var store = new InMemoryTaskStore();
        var (ca, sa) = InMemoryTransport.CreatePair();
        var (cb, sb) = InMemoryTransport.CreatePair();
        await using var serverA = new McpServer(sa, new McpServerOptions { TaskStore = store });
        await using var serverB = new McpServer(sb, new McpServerOptions { TaskStore = store });
        serverA.AddTool("quick", "quick", (_, _) => Task.FromResult(CallToolResult.Text("done")));
        var runA = serverA.RunAsync(timeout.Token);
        var runB = serverB.RunAsync(timeout.Token);
        await using var a = await McpClient.ConnectAsync(ca, cancellationToken: timeout.Token);
        await using var b = await McpClient.ConnectAsync(cb, cancellationToken: timeout.Token);
        var task = await a.CallToolAsTaskAsync("quick", ct: timeout.Token);
        await a.GetTaskResultAsync(task.Task.TaskId, timeout.Token);
        Assert.Empty(await b.ListTasksAsync(timeout.Token));
        await Assert.ThrowsAsync<McpException>(() => b.GetTaskAsync(task.Task.TaskId, timeout.Token));
        await Assert.ThrowsAsync<McpException>(() => b.GetTaskResultAsync(task.Task.TaskId, timeout.Token));
        await Assert.ThrowsAsync<McpException>(() => b.CancelTaskAsync(task.Task.TaskId, timeout.Token));
    }
    private sealed class InvalidResultHandler : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) =>
            Task.FromResult(new ElicitResult { Action = "invalid" });
    }

    [Fact]
    public async Task InvalidClientTaskResult_IsAnRpcError()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (ct, st) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(st);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(ct, new McpClientOptions
        {
            ElicitationHandler = new InvalidResultHandler()
        }, cancellationToken: timeout.Token);
        var request = ElicitRequest.Form("input", new ElicitationSchema { Properties = new Dictionary<string, PrimitiveSchemaDefinition>() });
        var ordinary = await Assert.ThrowsAsync<McpException>(() => server.ElicitAsync(request, timeout.Token));
        var task = await server.ElicitAsTaskAsync(request, cancellationToken: timeout.Token);
        var retained = await Assert.ThrowsAsync<McpException>(() => server.GetClientTaskResultAsync(task.Task.TaskId, timeout.Token));
        Assert.Equal(McpErrorCodes.InternalError, retained.ErrorCode);
        Assert.Equal(ordinary.Message, retained.Message);
    }

}
