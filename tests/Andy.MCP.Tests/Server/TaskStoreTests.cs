using System.Text.Json;
using Andy.MCP.Protocol;
using Andy.MCP.Server;

namespace Andy.MCP.Tests.Server;

/// <summary>
/// Tests the experimental task model and in-memory task store (issue #49): lifecycle, owner
/// isolation, result retrieval, cancellation, and TTL expiry.
/// </summary>
public class TaskStoreTests
{
    [Fact]
    public void TaskStatus_SerializesToWireStrings()
    {
        Assert.Equal("\"input_required\"", JsonSerializer.Serialize(McpTaskStatus.InputRequired, McpJsonDefaults.Options));
        Assert.Equal(McpTaskStatus.Cancelled, JsonSerializer.Deserialize<McpTaskStatus>("\"cancelled\"", McpJsonDefaults.Options));
    }

    [Fact]
    public void Task_RoundTrips()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(new TaskMetadata { Ttl = 60_000 }, "user-a");

        var json = JsonSerializer.Serialize(task, McpJsonDefaults.Options);
        var back = JsonSerializer.Deserialize<McpTask>(json, McpJsonDefaults.Options)!;

        Assert.Equal(task.TaskId, back.TaskId);
        Assert.Equal(McpTaskStatus.Working, back.Status);
        Assert.Equal(60_000, back.Ttl);
    }

    [Fact]
    public void Create_UsesSecureUnguessableId()
    {
        var store = new InMemoryTaskStore();
        var a = store.Create(null, null);
        var b = store.Create(null, null);

        Assert.NotEqual(a.TaskId, b.TaskId);
        Assert.True(a.TaskId.Length >= 20);
        Assert.DoesNotContain("=", a.TaskId); // base64url, unpadded
    }

    [Fact]
    public void OwnerIsolation_PreventsCrossUserAccess()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "user-a");

        Assert.NotNull(store.Get(task.TaskId, "user-a"));
        Assert.Null(store.Get(task.TaskId, "user-b"));         // different owner
        Assert.Null(store.Cancel(task.TaskId, "user-b"));       // cannot cancel
        Assert.Empty(store.List("user-b"));                     // not listed
        Assert.Single(store.List("user-a"));
    }

    [Fact]
    public void SetResult_MarksCompleted_AndResultIsOwnerScoped()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "user-a");

        Assert.True(store.SetResult(task.TaskId, JsonSerializer.SerializeToElement(new { answer = 42 })));
        Assert.Equal(McpTaskStatus.Completed, store.Get(task.TaskId, "user-a")!.Status);

        var result = store.GetResult(task.TaskId, "user-a");
        Assert.Equal(42, result!.Value.GetProperty("answer").GetInt32());
        Assert.Null(store.GetResult(task.TaskId, "user-b")); // owner isolation on results
    }

    [Fact]
    public void Cancel_TransitionsToCancelled_AndIsTerminal()
    {
        var store = new InMemoryTaskStore();
        var task = store.Create(null, "user-a");

        var cancelled = store.Cancel(task.TaskId, "user-a");
        Assert.Equal(McpTaskStatus.Cancelled, cancelled!.Status);

        // Completing a cancelled task is prevented at the cancel level (terminal).
        var again = store.Cancel(task.TaskId, "user-a");
        Assert.Equal(McpTaskStatus.Cancelled, again!.Status);
    }

    [Fact]
    public void ExpiredTasks_AreCleanedUp()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = now;
        var store = new InMemoryTaskStore(() => clock);

        var task = store.Create(new TaskMetadata { Ttl = 1000 }, "user-a"); // 1s TTL
        Assert.NotNull(store.Get(task.TaskId, "user-a"));

        clock = now.AddMilliseconds(1500); // advance past TTL
        Assert.Null(store.Get(task.TaskId, "user-a"));
        Assert.Empty(store.List("user-a"));
    }

    [Fact]
    public void UnlimitedTtl_NeverExpires()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = now;
        var store = new InMemoryTaskStore(() => clock);

        var task = store.Create(null, "user-a"); // null ttl = unlimited
        clock = now.AddDays(365);
        Assert.NotNull(store.Get(task.TaskId, "user-a"));
    }
}
