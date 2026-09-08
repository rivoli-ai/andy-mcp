using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

public class TaskStateMachineTests
{
    public static IEnumerable<object[]> Transitions() =>
        from start in Enum.GetValues<McpTaskStatus>()
        from end in Enum.GetValues<McpTaskStatus>()
        select new object[] { start, end };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void OnlyNonterminalTasksCanTransition(McpTaskStatus start, McpTaskStatus end)
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "owner");
        Assert.True(store.UpdateStatus(task.TaskId, start));
        var allowed = start is McpTaskStatus.Working or McpTaskStatus.InputRequired;
        Assert.Equal(allowed, store.UpdateStatus(task.TaskId, end));
        Assert.Equal(allowed ? end : start, store.Get(task.TaskId, "owner")!.Status);
    }

    [Theory]
    [InlineData(McpTaskStatus.Completed)]
    [InlineData(McpTaskStatus.Failed)]
    [InlineData(McpTaskStatus.Cancelled)]
    public void TerminalTasksRejectEveryLateWriter(McpTaskStatus status)
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "owner");
        store.UpdateStatus(task.TaskId, status);
        Assert.Null(store.Cancel(task.TaskId, "owner"));
        Assert.False(store.SetResult(task.TaskId, McpJsonDefaults.ToElement(new { success = true })));
        Assert.False(store.SetFailed(task.TaskId, "late"));
        Assert.Equal(status, store.Get(task.TaskId, "owner")!.Status);
    }

    [Fact]
    public void ExpiryBoundaryBlocksReadsAndWrites_AndHugeTtlDoesNotOverflow()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new InMemoryTaskStore(() => now);
        var expired = store.Create(new TaskMetadata { Ttl = 1000 }, "owner");
        var retained = store.Create(new TaskMetadata { Ttl = long.MaxValue }, "owner");
        now = now.AddMilliseconds(1000);
        Assert.False(store.SetResult(expired.TaskId, McpJsonDefaults.ToElement(new { answer = 1 })));
        Assert.False(store.UpdateStatus(expired.TaskId, McpTaskStatus.InputRequired));
        Assert.Null(store.Get(expired.TaskId, "owner"));
        Assert.Equal(retained.TaskId, Assert.Single(store.List("owner")).TaskId);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Create(new TaskMetadata { Ttl = -1 }, "owner"));
    }

    [Fact]
    public void FailedToolPayloadIsRetained_AndDetachedFromItsJsonDocument()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "owner");
        using (var document = JsonDocument.Parse("""{"isError":true,"content":[{"type":"text","text":"failed"}],"_meta":{"vendor":7}}"""))
            Assert.True(store.SetResult(task.TaskId, document.RootElement));
        Assert.Equal(McpTaskStatus.Failed, store.Get(task.TaskId, "owner")!.Status);
        Assert.Equal(7, store.GetResult(task.TaskId, "owner")!.Value.GetProperty("_meta").GetProperty("vendor").GetInt32());
        Assert.Null(store.GetResult(task.TaskId, "other"));
    }

    [Fact]
    public void TimestampsRepresentUtc_AndInvalidStatusDoesNotMutateTheTask()
    {
        var clock = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.FromHours(2));
        var store = new InMemoryTaskStore(() => clock);
        var task = store.Create(null, null);
        Assert.Equal("2026-09-08T08:00:00.000Z", task.CreatedAt);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdateStatus(task.TaskId, (McpTaskStatus)999));
        Assert.Equal(McpTaskStatus.Working, store.Get(task.TaskId, null)!.Status);
    }

    [Fact]
    public async Task ConcurrentCompletionAndCancellationHaveOneTerminalWinner()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var store = new InMemoryTaskStore();
            var task = store.Create(null, "owner");
            using var gate = new ManualResetEventSlim();
            var complete = Task.Run(() => { gate.Wait(); return store.SetResult(task.TaskId, McpJsonDefaults.ToElement(new { answer = 1 })); });
            var cancel = Task.Run(() => { gate.Wait(); return store.Cancel(task.TaskId, "owner"); });
            gate.Set();
            await Task.WhenAll(complete, cancel);
            Assert.NotEqual(await complete, await cancel is not null);
            Assert.Equal(await complete ? McpTaskStatus.Completed : McpTaskStatus.Cancelled, store.Get(task.TaskId, "owner")!.Status);
            Assert.False(store.SetFailed(task.TaskId));
        }
    }
}
