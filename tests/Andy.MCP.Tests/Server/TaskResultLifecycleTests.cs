using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class TaskResultLifecycleTests
{
    private static JsonRpcRequest ResultRequest(string id) => new()
    {
        Id = 1,
        Method = McpMethods.TasksResult,
        Params = McpJsonDefaults.ToElement(new { taskId = id })
    };

    [Fact]
    public async Task InputRequired_WaitsForCompletion_AndPreservesMetadata()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "owner");
        store.UpdateStatus(task.TaskId, McpTaskStatus.InputRequired);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = TaskResults.WaitAsync(store, "owner", ResultRequest(task.TaskId), timeout.Token);
        var second = TaskResults.WaitAsync(store, "owner", ResultRequest(task.TaskId), timeout.Token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        store.UpdateStatus(task.TaskId, McpTaskStatus.Working);
        store.SetResult(task.TaskId, McpJsonDefaults.ToElement(new { content = Array.Empty<object>(), isError = true, _meta = new { vendor = 7 } }));
        foreach (var response in await Task.WhenAll(first, second))
        {
            Assert.False(response.IsError);
            var meta = response.Result!.Value.GetProperty("_meta");
            Assert.Equal(7, meta.GetProperty("vendor").GetInt32());
            Assert.Equal(task.TaskId, meta.GetProperty("io.modelcontextprotocol/related-task").GetProperty("taskId").GetString());
            Assert.True(response.Result.Value.GetProperty("isError").GetBoolean());
        }
        Assert.False(store.GetResult(task.TaskId, "owner")!.Value.GetProperty("_meta").TryGetProperty("io.modelcontextprotocol/related-task", out _));
    }

    [Fact]
    public async Task AbandonedWait_DoesNotCancelTask_AndLaterRetrievalSucceeds()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, null);
        using var cancellation = new CancellationTokenSource();
        var pending = TaskResults.WaitAsync(store, null, ResultRequest(task.TaskId), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(McpTaskStatus.Working, store.Get(task.TaskId, null)!.Status);
        store.SetResult(task.TaskId, McpJsonDefaults.ToElement(new { answer = 42 }));
        var response = await TaskResults.WaitAsync(store, null, ResultRequest(task.TaskId), default);
        Assert.Equal(42, response.Result!.Value.GetProperty("answer").GetInt32());
    }

    [Fact]
    public async Task ExpiryAndOwnership_AreCheckedWhileWaiting()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryTaskStore(() => now);
        var task = store.Create(new TaskMetadata { Ttl = 1000 }, "owner");
        var unauthorized = await TaskResults.WaitAsync(store, "other", ResultRequest(task.TaskId), default);
        Assert.Equal(McpErrorCodes.InvalidParams, unauthorized.Error!.Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = TaskResults.WaitAsync(store, "owner", ResultRequest(task.TaskId), timeout.Token);
        now = now.AddSeconds(1);
        Assert.Equal(McpErrorCodes.InvalidParams, (await pending).Error!.Code);
    }

    [Theory]
    [InlineData(McpTaskStatus.Cancelled, McpErrorCodes.InvalidRequest)]
    [InlineData(McpTaskStatus.Failed, McpErrorCodes.InternalError)]
    [InlineData(McpTaskStatus.Completed, McpErrorCodes.InternalError)]
    public async Task TerminalStatesWithoutPayload_ReturnErrors(McpTaskStatus status, int code)
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, null);
        store.UpdateStatus(task.TaskId, status, "original failure");
        var response = await TaskResults.WaitAsync(store, null, ResultRequest(task.TaskId), default);
        Assert.Equal(code, response.Error!.Code);
    }

    [Fact]
    public async Task CancellingToolTask_StopsExecution_AndReleasesPendingResult()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        server.AddTool("wait", "wait", async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { stopped.SetResult(); }
            return CallToolResult.Text("unreachable");
        });
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport, cancellationToken: timeout.Token);
        var created = await client.CallToolAsTaskAsync("wait", ct: timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var result = client.GetTaskResultAsync(created.Task.TaskId, timeout.Token);
        var cancelled = await client.CancelTaskAsync(created.Task.TaskId, timeout.Token);
        Assert.Equal(McpTaskStatus.Cancelled, cancelled.Status);
        await stopped.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<McpException>(() => result);
        await Assert.ThrowsAsync<McpException>(() => client.CancelTaskAsync(created.Task.TaskId, timeout.Token));
        Assert.Equal(McpTaskStatus.Cancelled, (await client.GetTaskAsync(created.Task.TaskId, timeout.Token)).Status);
    }

    private sealed class CancellableSampling : ISamplingHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Stopped.SetResult(); }
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task CancellingSamplingTask_StopsClientExecution_AndReleasesPendingResult()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CancellableSampling();
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        await using var server = new McpServer(serverTransport);
        var run = server.RunAsync(timeout.Token);
        await using var client = await McpClient.ConnectAsync(clientTransport,
            new McpClientOptions { SamplingHandler = handler }, cancellationToken: timeout.Token);
        var created = await server.CreateMessageAsTaskAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent("hi")] }],
            MaxTokens = 10
        }, cancellationToken: timeout.Token);
        await handler.Entered.Task.WaitAsync(timeout.Token);
        var result = server.GetClientTaskResultAsync(created.Task.TaskId, timeout.Token);
        Assert.Equal(McpTaskStatus.Cancelled, (await server.CancelClientTaskAsync(created.Task.TaskId, timeout.Token)).Status);
        await handler.Stopped.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAsync<McpException>(() => result);
        Assert.Equal(McpTaskStatus.Cancelled, (await server.GetClientTaskAsync(created.Task.TaskId, timeout.Token)).Status);
    }
}
