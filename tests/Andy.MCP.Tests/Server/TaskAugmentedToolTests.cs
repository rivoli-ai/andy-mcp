using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// End-to-end tests for task-augmented tool execution and the tasks/* query operations (issue #49).
/// </summary>
public class TaskAugmentedToolTests
{
    private static async Task<McpClient> ConnectAsync(Action<McpServer> configure, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport);
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    private static async Task<McpTask> PollUntilAsync(McpClient client, string taskId, McpTaskStatus status, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            var task = await client.GetTaskAsync(taskId, ct);
            if (task.Status == status)
                return task;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException($"Task did not reach {status}.");
    }

    [Fact]
    public async Task TaskAugmentedTool_ReturnsTask_ThenDeferredResult()
    {
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("slow", "d", async (_, _) =>
            {
                await release.Task;
                return CallToolResult.Text("finished");
            }), cts.Token);

        // The augmented call returns immediately with a working task.
        var created = await client.CallToolAsTaskAsync("slow", ttlMs: 60_000, ct: cts.Token);
        Assert.Equal(McpTaskStatus.Working, created.Task.Status);
        Assert.False(string.IsNullOrEmpty(created.Task.TaskId));

        // Result is not available while the tool runs.
        await Assert.ThrowsAsync<McpException>(() => client.GetTaskResultAsync(created.Task.TaskId, cts.Token));

        // Let the tool finish, then poll to completion and fetch the deferred result.
        release.SetResult();
        var done = await PollUntilAsync(client, created.Task.TaskId, McpTaskStatus.Completed, cts.Token);
        Assert.Equal(McpTaskStatus.Completed, done.Status);

        var payload = await client.GetTaskResultAsync(created.Task.TaskId, cts.Token);
        var result = payload.Deserialize<CallToolResult>(McpJsonDefaults.Options)!;
        Assert.Equal("finished", ((TextContent)result.Content[0]).Text);
    }

    [Fact]
    public async Task ListTasks_ReturnsCreatedTask()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok"))), cts.Token);

        var created = await client.CallToolAsTaskAsync("t", ttlMs: 60_000, ct: cts.Token);
        var tasks = await client.ListTasksAsync(cts.Token);

        Assert.Contains(tasks, t => t.TaskId == created.Task.TaskId);
    }

    [Fact]
    public async Task CancelTask_TransitionsToCancelled()
    {
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("slow", "d", async (_, _) => { await release.Task; return CallToolResult.Text("x"); }), cts.Token);

        var created = await client.CallToolAsTaskAsync("slow", ttlMs: 60_000, ct: cts.Token);
        var cancelled = await client.CancelTaskAsync(created.Task.TaskId, cts.Token);

        Assert.Equal(McpTaskStatus.Cancelled, cancelled.Status);
        release.SetResult();
    }

    [Fact]
    public async Task GetTask_UnknownId_ReturnsError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var client = await ConnectAsync(s =>
            s.AddTool("t", "d", (_, _) => Task.FromResult(CallToolResult.Text("ok"))), cts.Token);

        await Assert.ThrowsAsync<McpException>(() => client.GetTaskAsync("does-not-exist", cts.Token));
    }
}
