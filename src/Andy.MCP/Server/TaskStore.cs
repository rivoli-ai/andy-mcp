using System.Security.Cryptography;
using System.Text.Json;
using Andy.MCP.Protocol;

namespace Andy.MCP.Server;

/// <summary>
/// A durable store for experimental MCP tasks (MCP 2025-11-25). Implementations own task identity,
/// state transitions, result storage, TTL expiry, and authorization isolation so that a caller can
/// only see and act on tasks it owns.
/// </summary>
public interface ITaskStore
{
    /// <summary>Create a new task owned by <paramref name="ownerKey"/> and return its initial state.</summary>
    McpTask Create(TaskMetadata? metadata, string? ownerKey);

    /// <summary>Get a task if it exists, is not expired, and is owned by <paramref name="ownerKey"/>.</summary>
    McpTask? Get(string taskId, string? ownerKey);

    /// <summary>List the non-expired tasks owned by <paramref name="ownerKey"/>.</summary>
    IReadOnlyList<McpTask> List(string? ownerKey);

    /// <summary>Cancel an owned task, returning its updated state, or null if not found/authorized.</summary>
    McpTask? Cancel(string taskId, string? ownerKey);

    /// <summary>Store the result payload for a task and mark it completed (server-internal).</summary>
    bool SetResult(string taskId, JsonElement result);

    /// <summary>Mark a task failed with an optional message (server-internal).</summary>
    bool SetFailed(string taskId, string? statusMessage = null);

    /// <summary>Update a task's status (server-internal).</summary>
    bool UpdateStatus(string taskId, McpTaskStatus status, string? statusMessage = null);

    /// <summary>Retrieve a completed task's result payload for an owned task.</summary>
    JsonElement? GetResult(string taskId, string? ownerKey);
}

/// <summary>
/// In-memory reference implementation of <see cref="ITaskStore"/>. Thread-safe, uses
/// cryptographically-secure task ids, and enforces owner isolation and TTL expiry. A clock is
/// injectable so TTL behavior is deterministically testable.
/// </summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private sealed class Entry
    {
        public required McpTask Task { get; set; }
        public string? OwnerKey { get; init; }
        public JsonElement? Result { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private readonly Dictionary<string, Entry> _tasks = new();
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryTaskStore(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public McpTask Create(TaskMetadata? metadata, string? ownerKey)
    {
        var now = _clock();
        var timestamp = Iso(now);
        var task = new McpTask
        {
            TaskId = GenerateTaskId(),
            Status = McpTaskStatus.Working,
            CreatedAt = timestamp,
            LastUpdatedAt = timestamp,
            Ttl = metadata?.Ttl
        };

        lock (_sync)
        {
            _tasks[task.TaskId] = new Entry { Task = task, OwnerKey = ownerKey, CreatedAt = now };
        }
        return task;
    }

    public McpTask? Get(string taskId, string? ownerKey)
    {
        lock (_sync)
        {
            return TryGetAuthorized(taskId, ownerKey, out var entry) ? entry.Task : null;
        }
    }

    public IReadOnlyList<McpTask> List(string? ownerKey)
    {
        lock (_sync)
        {
            PurgeExpired();
            return _tasks.Values
                .Where(e => OwnerMatches(e, ownerKey))
                .Select(e => e.Task)
                .ToList();
        }
    }

    public McpTask? Cancel(string taskId, string? ownerKey)
    {
        lock (_sync)
        {
            if (!TryGetAuthorized(taskId, ownerKey, out var entry))
                return null;

            // Terminal tasks are not re-cancelled.
            if (entry.Task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled)
                return entry.Task;

            entry.Task = Touch(entry.Task) with { Status = McpTaskStatus.Cancelled };
            return entry.Task;
        }
    }

    public bool SetResult(string taskId, JsonElement result)
    {
        lock (_sync)
        {
            if (!_tasks.TryGetValue(taskId, out var entry))
                return false;
            entry.Result = result;
            entry.Task = Touch(entry.Task) with { Status = McpTaskStatus.Completed };
            return true;
        }
    }

    public bool SetFailed(string taskId, string? statusMessage = null) =>
        UpdateStatus(taskId, McpTaskStatus.Failed, statusMessage);

    public bool UpdateStatus(string taskId, McpTaskStatus status, string? statusMessage = null)
    {
        lock (_sync)
        {
            if (!_tasks.TryGetValue(taskId, out var entry))
                return false;
            entry.Task = Touch(entry.Task) with { Status = status, StatusMessage = statusMessage ?? entry.Task.StatusMessage };
            return true;
        }
    }

    public JsonElement? GetResult(string taskId, string? ownerKey)
    {
        lock (_sync)
        {
            return TryGetAuthorized(taskId, ownerKey, out var entry) ? entry.Result : null;
        }
    }

    // --- helpers (all called under _sync) ---

    private bool TryGetAuthorized(string taskId, string? ownerKey, out Entry entry)
    {
        PurgeExpired();
        if (_tasks.TryGetValue(taskId, out var found) && OwnerMatches(found, ownerKey))
        {
            entry = found;
            return true;
        }
        entry = null!;
        return false;
    }

    private static bool OwnerMatches(Entry entry, string? ownerKey) =>
        string.Equals(entry.OwnerKey, ownerKey, StringComparison.Ordinal);

    private void PurgeExpired()
    {
        var now = _clock();
        var expired = _tasks
            .Where(kvp => kvp.Value.Task.Ttl is { } ttl && now - kvp.Value.CreatedAt > TimeSpan.FromMilliseconds(ttl))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
            _tasks.Remove(key);
    }

    private McpTask Touch(McpTask task) => task with { LastUpdatedAt = Iso(_clock()) };

    private static string Iso(DateTimeOffset time) => time.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string GenerateTaskId()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
