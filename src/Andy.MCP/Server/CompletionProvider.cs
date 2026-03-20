using System.Text.Json.Serialization;

namespace Andy.MCP.Server;

/// <summary>
/// Registered completion handler for a specific prompt or resource argument.
/// </summary>
public sealed class CompletionRegistration
{
    public required string RefType { get; init; } // "ref/prompt" or "ref/resource"
    public required string RefName { get; init; } // prompt name or resource URI
    public required string ArgumentName { get; init; }
    public required Func<string, IDictionary<string, string>?, CancellationToken, Task<CompletionValues>> Handler { get; init; }
}

/// <summary>
/// Result from a completion handler.
/// </summary>
public sealed record CompletionValues
{
    public required IReadOnlyList<string> Values { get; init; }
    public int? Total { get; init; }
    public bool HasMore { get; init; }
}

/// <summary>
/// Request params for completion/complete.
/// </summary>
public sealed record CompletionRequest
{
    [JsonPropertyName("ref")]
    public required CompletionRef Ref { get; init; }

    [JsonPropertyName("argument")]
    public required CompletionArgument Argument { get; init; }

    [JsonPropertyName("context")]
    public CompletionContext? Context { get; init; }
}

public sealed record CompletionRef
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uri { get; init; }
}

public sealed record CompletionArgument
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed record CompletionContext
{
    [JsonPropertyName("arguments")]
    public IDictionary<string, string>? Arguments { get; init; }
}

public sealed record CompletionResult
{
    [JsonPropertyName("completion")]
    public required CompletionData Completion { get; init; }
}

public sealed record CompletionData
{
    [JsonPropertyName("values")]
    public required IReadOnlyList<string> Values { get; init; }

    [JsonPropertyName("total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}
