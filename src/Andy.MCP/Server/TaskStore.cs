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

    /// <summary>Cancel an owned task, returning its updated state, or null if missing, unauthorized, expired, or already terminal.</summary>
    McpTask? Cancel(string taskId, string? ownerKey);

    /// <summary>Atomically retain an object result and finish a nonterminal task; tool results with isError=true mark it failed.</summary>
    bool SetResult(string taskId, JsonElement result);

    /// <summary>Mark a task failed with an optional message (server-internal).</summary>
    bool SetFailed(string taskId, string? statusMessage = null);

    /// <summary>
    /// Atomically retain a JSON-RPC error and mark a nonterminal task failed. Durable stores must
    /// override this and GetError to preserve error code, data and extensions across restarts.
    /// The default adapts legacy stores by retaining only the status message through SetFailed.
    /// </summary>
    bool SetError(string taskId, JsonRpcError error) => SetFailed(taskId, error.Message);

    /// <summary>Retrieve an owned task's retained RPC error. Legacy stores return null.</summary>
    JsonRpcError? GetError(string taskId, string? ownerKey) => null;

    /// <summary>Update a task's status (server-internal).</summary>
    bool UpdateStatus(string taskId, McpTaskStatus status, string? statusMessage = null);

    /// <summary>Retrieve a retained result payload for an owned task, including failed tool results.</summary>
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
        public JsonRpcError? Error { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
    }

    private readonly Dictionary<string, Entry> _tasks = new();
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryTaskStore(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public McpTask Create(TaskMetadata? metadata, string? ownerKey)
    {
        if (metadata?.Ttl is < 0) throw new ArgumentOutOfRangeException(nameof(metadata), "Task TTL must be nonnegative.");
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

            // Cancellation of any terminal state is invalid, including repeated cancellation.
            if (entry.Task.Status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled)
                return null;

            entry.Task = Touch(entry.Task) with { Status = McpTaskStatus.Cancelled };
            return entry.Task;
        }
    }

    public bool SetResult(string taskId, JsonElement result)
    {
        lock (_sync)
        {
            PurgeExpired();
            if (!_tasks.TryGetValue(taskId, out var entry) || IsTerminal(entry.Task.Status))
                return false;
            if (result.ValueKind != JsonValueKind.Object) throw new ArgumentException("Task results must be JSON objects.", nameof(result));
            entry.Result = result.Clone();
            var failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
            entry.Task = Touch(entry.Task) with { Status = failed ? McpTaskStatus.Failed : McpTaskStatus.Completed };
            return true;
        }
    }

    public bool SetFailed(string taskId, string? statusMessage = null) =>
        SetError(taskId, JsonRpcError.InternalError(statusMessage));

    public bool SetError(string taskId, JsonRpcError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            PurgeExpired();
            if (!_tasks.TryGetValue(taskId, out var entry) || IsTerminal(entry.Task.Status)) return false;
            // Detach error data and extensions from caller-owned JsonDocuments.
            entry.Error = McpJsonDefaults.ToElement(error).Deserialize<JsonRpcError>(McpJsonDefaults.Options)!;
            entry.Task = Touch(entry.Task) with { Status = McpTaskStatus.Failed, StatusMessage = error.Message };
            return true;
        }
    }

    public JsonRpcError? GetError(string taskId, string? ownerKey)
    {
        lock (_sync)
            return TryGetAuthorized(taskId, ownerKey, out var entry) ? entry.Error : null;
    }

    public bool UpdateStatus(string taskId, McpTaskStatus status, string? statusMessage = null)
    {
        lock (_sync)
        {
            PurgeExpired();
            if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
            if (!_tasks.TryGetValue(taskId, out var entry) || IsTerminal(entry.Task.Status))
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
            .Where(kvp => kvp.Value.Task.Ttl is { } ttl && (now - kvp.Value.CreatedAt).TotalMilliseconds >= ttl)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in expired)
            _tasks.Remove(key);
    }

    private static bool IsTerminal(McpTaskStatus status) => status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;

    private McpTask Touch(McpTask task) => task with { LastUpdatedAt = Iso(_clock()) };

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    private static string GenerateTaskId()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
