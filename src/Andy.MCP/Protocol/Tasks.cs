using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// State of an experimental MCP task (MCP 2025-11-25). Named McpTaskStatus to avoid clashing
/// with <see cref="System.Threading.Tasks.TaskStatus"/>.
/// </summary>
[JsonConverter(typeof(McpTaskStatusJsonConverter))]
public enum McpTaskStatus
{
    /// <summary>The request is currently being processed.</summary>
    Working,

    /// <summary>The task is waiting for input (e.g. elicitation or sampling).</summary>
    InputRequired,

    /// <summary>The request completed successfully and results are available.</summary>
    Completed,

    /// <summary>The associated request did not complete successfully.</summary>
    Failed,

    /// <summary>The request was cancelled before completion.</summary>
    Cancelled
}

/// <summary>
/// An experimental MCP task representing a durable request tracked for polling and deferred
/// result retrieval (MCP 2025-11-25).
/// </summary>
public sealed record McpTask
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("status")]
    public required McpTaskStatus Status { get; init; }

    [JsonPropertyName("statusMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusMessage { get; init; }

    /// <summary>ISO 8601 timestamp when the task was created.</summary>
    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }

    /// <summary>ISO 8601 timestamp when the task was last updated.</summary>
    [JsonPropertyName("lastUpdatedAt")]
    public required string LastUpdatedAt { get; init; }

    /// <summary>Actual retention duration from creation in milliseconds, or null for unlimited.</summary>
    [JsonPropertyName("ttl")]
    public long? Ttl { get; init; }

    /// <summary>Suggested polling interval in milliseconds.</summary>
    [JsonPropertyName("pollInterval")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PollInterval { get; init; }
}

/// <summary>Task augmentation metadata attached to a request to run it as a task.</summary>
public sealed record TaskMetadata
{
    /// <summary>Requested retention duration in milliseconds from creation.</summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Ttl { get; init; }
}

/// <summary>Result returned immediately when a request is executed as a task.</summary>
public sealed record CreateTaskResult
{
    [JsonPropertyName("task")]
    public required McpTask Task { get; init; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>Result of tasks/list.</summary>
public sealed record ListTasksResult : PaginatedResult
{
    [JsonPropertyName("tasks")]
    public IReadOnlyList<McpTask> Tasks { get; init; } = [];
}

/// <summary>Params carrying just a task identifier (tasks/get, tasks/result, tasks/cancel).</summary>
public sealed record TaskIdParams
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }
}

/// <summary>Serializes <see cref="McpTaskStatus"/> to its wire string form.</summary>
public sealed class McpTaskStatusJsonConverter : JsonConverter<McpTaskStatus>
{
    public override McpTaskStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "working" => McpTaskStatus.Working,
            "input_required" => McpTaskStatus.InputRequired,
            "completed" => McpTaskStatus.Completed,
            "failed" => McpTaskStatus.Failed,
            "cancelled" => McpTaskStatus.Cancelled,
            var other => throw new JsonException($"Unknown task status '{other}'.")
        };

    public override void Write(Utf8JsonWriter writer, McpTaskStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            McpTaskStatus.Working => "working",
            McpTaskStatus.InputRequired => "input_required",
            McpTaskStatus.Completed => "completed",
            McpTaskStatus.Failed => "failed",
            McpTaskStatus.Cancelled => "cancelled",
            _ => throw new JsonException($"Unknown task status '{value}'.")
        });
}
