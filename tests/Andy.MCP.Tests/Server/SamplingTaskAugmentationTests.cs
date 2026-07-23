using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// End-to-end tests for task-augmented sampling and elicitation (issue #72): the server sends a
/// task-augmented request, the client creates the task and runs the handler in the background, and
/// the server retrieves the deferred result via the task APIs.
/// </summary>
public class SamplingTaskAugmentationTests
{
    private sealed class BlockingSamplingHandler : ISamplingHandler
    {
        private readonly Task _gate;
        public BlockingSamplingHandler(Task gate) => _gate = gate;

        public async Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken ct)
        {
            await _gate;
            return new CreateMessageResult
            {
                Role = Role.Assistant,
                Content = [new TextContent { Text = "hello" }],
                Model = "assistant-model"
            };
        }
    }

    private sealed class AcceptElicitationHandler : IElicitationHandler
    {
        public Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken ct) =>
            Task.FromResult(ElicitResult.Accept(JsonSerializer.SerializeToElement(new { ok = true })));
    }

    private static async Task<McpTask> PollUntilCompletedAsync(McpServer server, string taskId, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            var task = await server.GetClientTaskAsync(taskId, ct);
            if (task.Status == McpTaskStatus.Completed)
                return task;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Client task did not complete.");
    }

    private static async Task<(McpServer server, McpClient client)> ConnectAsync(
        McpClientOptions clientOptions, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        _ = server.RunAsync(ct);
        var client = await McpClient.ConnectAsync(clientTransport, clientOptions, cancellationToken: ct);
        return (server, client);
    }

    [Fact]
    public async Task Sampling_AsTask_ReturnsTask_ThenDeferredResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var release = new TaskCompletionSource();
        var (server, client) = await ConnectAsync(
            new McpClientOptions { SamplingHandler = new BlockingSamplingHandler(release.Task) }, cts.Token);
        await using var _ = client;

        var created = await server.CreateMessageAsTaskAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10
        }, ttlMs: 60_000, cancellationToken: cts.Token);

        Assert.Equal(McpTaskStatus.Working, created.Task.Status);

        // The result is not available while the client's handler is blocked.
        await Assert.ThrowsAsync<McpException>(() => server.GetClientTaskResultAsync(created.Task.TaskId, cts.Token));

        release.SetResult();
        await PollUntilCompletedAsync(server, created.Task.TaskId, cts.Token);

        var payload = await server.GetClientTaskResultAsync(created.Task.TaskId, cts.Token);
        var result = payload.Deserialize<CreateMessageResult>(McpJsonDefaults.Options)!;
        Assert.Equal("assistant-model", result.Model);
    }

    [Fact]
    public async Task Elicitation_AsTask_ReturnsDeferredResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (server, client) = await ConnectAsync(
            new McpClientOptions { ElicitationHandler = new AcceptElicitationHandler() }, cts.Token);
        await using var _ = client;

        var created = await server.ElicitAsTaskAsync(
            new ElicitRequest { Message = "confirm?" }, ttlMs: 60_000, cancellationToken: cts.Token);

        await PollUntilCompletedAsync(server, created.Task.TaskId, cts.Token);

        var payload = await server.GetClientTaskResultAsync(created.Task.TaskId, cts.Token);
        var result = payload.Deserialize<ElicitResult>(McpJsonDefaults.Options)!;
        Assert.Equal("accept", result.Action);
    }

    [Fact]
    public async Task Cancel_ClientTask_TransitionsToCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var release = new TaskCompletionSource();
        var (server, client) = await ConnectAsync(
            new McpClientOptions { SamplingHandler = new BlockingSamplingHandler(release.Task) }, cts.Token);
        await using var _ = client;

        var created = await server.CreateMessageAsTaskAsync(new CreateMessageRequest
        {
            Messages = [new SamplingMessage { Role = Role.User, Content = [new TextContent { Text = "hi" }] }],
            MaxTokens = 10
        }, ttlMs: 60_000, cancellationToken: cts.Token);

        var cancelled = await server.CancelClientTaskAsync(created.Task.TaskId, cts.Token);
        Assert.Equal(McpTaskStatus.Cancelled, cancelled.Status);
        release.SetResult();
    }
}
