using System.Text.Json;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests task ownership isolation and durable retrieval across connections when servers share a
/// task store (issues #72 / #49).
/// </summary>
public class TaskOwnershipTests
{
    private static async Task<McpClient> ConnectAsync(
        ITaskStore store, string ownerKey, Action<McpServer> configure, CancellationToken ct)
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new McpServer(serverTransport, new McpServerOptions
        {
            TaskStore = store,
            TaskOwnerKey = ownerKey
        });
        configure(server);
        _ = server.RunAsync(ct);
        return await McpClient.ConnectAsync(clientTransport, cancellationToken: ct);
    }

    private static async Task<McpTask> PollUntilCompletedAsync(McpClient client, string taskId, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            var task = await client.GetTaskAsync(taskId, ct);
            if (task.Status == McpTaskStatus.Completed)
                return task;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException("Task did not complete.");
    }

    [Fact]
    public async Task DifferentOwners_CannotSeeEachOthersTasks()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var store = new InMemoryTaskStore();
        var release = new TaskCompletionSource();

        await using var clientA = await ConnectAsync(store, "user-a", s =>
            s.AddTool("slow", "d", async (_, _) => { await release.Task; return CallToolResult.Text("x"); }), cts.Token);
        await using var clientB = await ConnectAsync(store, "user-b", s =>
            s.AddTool("noop", "d", (_, _) => Task.FromResult(CallToolResult.Text("x"))), cts.Token);

        var created = await clientA.CallToolAsTaskAsync("slow", ttlMs: 60_000, ct: cts.Token);

        // B (a different owner) cannot list, inspect, or cancel A's task.
        Assert.DoesNotContain(await clientB.ListTasksAsync(cts.Token), t => t.TaskId == created.Task.TaskId);
        await Assert.ThrowsAsync<McpException>(() => clientB.GetTaskAsync(created.Task.TaskId, cts.Token));
        await Assert.ThrowsAsync<McpException>(() => clientB.CancelTaskAsync(created.Task.TaskId, cts.Token));

        // A owns it.
        Assert.Equal(created.Task.TaskId, (await clientA.GetTaskAsync(created.Task.TaskId, cts.Token)).TaskId);
        release.SetResult();
    }

    [Fact]
    public async Task Task_Result_IsRetrievable_FromANewConnection_ForTheSameOwner()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var store = new InMemoryTaskStore();

        string taskId;
        await using (var client1 = await ConnectAsync(store, "user-a", s =>
            s.AddTool("quick", "d", (_, _) => Task.FromResult(CallToolResult.Text("done"))), cts.Token))
        {
            var created = await client1.CallToolAsTaskAsync("quick", ttlMs: 60_000, ct: cts.Token);
            taskId = created.Task.TaskId;
            await PollUntilCompletedAsync(client1, taskId, cts.Token);
        } // original connection is gone

        // A brand-new connection for the same owner retrieves the durable result.
        await using var client2 = await ConnectAsync(store, "user-a", _ => { }, cts.Token);
        var payload = await client2.GetTaskResultAsync(taskId, cts.Token);
        var result = payload.Deserialize<CallToolResult>(McpJsonDefaults.Options)!;

        Assert.Equal("done", ((TextContent)result.Content[0]).Text);
    }
}
