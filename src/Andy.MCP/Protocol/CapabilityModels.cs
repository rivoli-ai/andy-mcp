using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

public sealed record ToolExecution
{
    [JsonPropertyName("taskSupport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskSupport { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ClientTasksCapability
{
    [JsonPropertyName("list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? List { get; init; }

    [JsonPropertyName("cancel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Cancel { get; init; }

    [JsonPropertyName("requests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientTaskRequests? Requests { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ServerTasksCapability
{
    [JsonPropertyName("list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? List { get; init; }

    [JsonPropertyName("cancel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Cancel { get; init; }

    [JsonPropertyName("requests")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerTaskRequests? Requests { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ClientTaskRequests
{
    [JsonPropertyName("sampling")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SamplingTaskRequests? Sampling { get; init; }

    [JsonPropertyName("elicitation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ElicitationTaskRequests? Elicitation { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ServerTaskRequests
{
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolTaskRequests? Tools { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record SamplingTaskRequests
{
    [JsonPropertyName("createMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? CreateMessage { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ElicitationTaskRequests
{
    [JsonPropertyName("create")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Create { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ToolTaskRequests
{
    [JsonPropertyName("call")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Call { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record RelatedTaskMetadata
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed record ElicitationCompleteParams
{
    [JsonPropertyName("elicitationId")]
    public required string ElicitationId { get; init; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>Task status notification parameters include the complete task state.</summary>
public sealed record TaskStatusParams
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }
    [JsonPropertyName("status")]
    public required McpTaskStatus Status { get; init; }
    [JsonPropertyName("createdAt")]
    public required string CreatedAt { get; init; }
    [JsonPropertyName("lastUpdatedAt")]
    public required string LastUpdatedAt { get; init; }
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? Ttl { get; init; }
    [JsonPropertyName("statusMessage")]
    public string? StatusMessage { get; init; }
    [JsonPropertyName("pollInterval")]
    public int? PollInterval { get; init; }
    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
