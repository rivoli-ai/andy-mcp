using System.Text.Json;
using System.Text.Json.Serialization;
using Andy.MCP.Protocol;

namespace Andy.MCP.Client;

#region Roots (#12)

/// <summary>
/// Provides filesystem roots to MCP servers.
/// </summary>
public interface IRootProvider
{
    /// <summary>
    /// Get the current list of roots.
    /// </summary>
    IReadOnlyList<Root> GetRoots();

    /// <summary>
    /// Fired when the root list changes. Client sends notifications/roots/list_changed.
    /// </summary>
    event EventHandler? RootsChanged;
}

/// <summary>
/// A filesystem root exposed to MCP servers.
/// </summary>
public sealed record Root
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

public sealed record ListRootsResult
{
    [JsonPropertyName("roots")]
    public required IReadOnlyList<Root> Roots { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Static root provider with a fixed list. Supports runtime updates.
/// </summary>
public sealed class StaticRootProvider : IRootProvider
{
    private readonly List<Root> _roots;
    private readonly object _lock = new();

    public event EventHandler? RootsChanged;

    public StaticRootProvider(params Root[] roots)
    {
        _roots = new List<Root>(roots);
    }

    public IReadOnlyList<Root> GetRoots()
    {
        lock (_lock) return _roots.ToList();
    }

    public void AddRoot(Root root)
    {
        lock (_lock) _roots.Add(root);
        RootsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveRoot(string uri)
    {
        bool removed;
        lock (_lock) removed = _roots.RemoveAll(r => r.Uri == uri) > 0;
        if (removed) RootsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}

#endregion

#region Sampling (#13)

/// <summary>
/// Handles LLM sampling requests from MCP servers.
/// </summary>
public interface ISamplingHandler
{
    Task<CreateMessageResult> HandleAsync(CreateMessageRequest request, CancellationToken cancellationToken);
}

public sealed record CreateMessageRequest
{
    [JsonPropertyName("messages")]
    public required IReadOnlyList<SamplingMessage> Messages { get; init; }

    [JsonPropertyName("modelPreferences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelPreferences? ModelPreferences { get; init; }

    [JsonPropertyName("systemPrompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemPrompt { get; init; }

    [JsonPropertyName("includeContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IncludeContext { get; init; }

    [JsonPropertyName("maxTokens")]
    public required int MaxTokens { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("stopSequences")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? StopSequences { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Metadata { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [SinceRevision("2025-11-25")]
    public IReadOnlyList<Tool>? Tools { get; init; }

    [JsonPropertyName("toolChoice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [SinceRevision("2025-11-25")]
    public ToolChoice? ToolChoice { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

public sealed record SamplingMessage
{
    [JsonPropertyName("role")]
    public required Role Role { get; init; }

    /// <summary>
    /// The message content. Per MCP 2025-11-25 this is a scalar-or-array union on the wire; it is
    /// normalized to a list here and always serialized as an array. Only valid sampling content
    /// blocks (text, image, audio, tool_use, tool_result) are permitted.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(SamplingContentConverter))]
    public required IReadOnlyList<Content> Content { get; init; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

public sealed record ModelPreferences
{
    [JsonPropertyName("hints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelHint>? Hints { get; init; }

    [JsonPropertyName("costPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CostPriority { get; init; }

    [JsonPropertyName("speedPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SpeedPriority { get; init; }

    [JsonPropertyName("intelligencePriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? IntelligencePriority { get; init; }
}

public sealed record ModelHint
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record CreateMessageResult
{
    [JsonPropertyName("role")]
    public required Role Role { get; init; }

    /// <summary>
    /// The generated message content. Per MCP 2025-11-25 this is a scalar-or-array union on the
    /// wire (CreateMessageResult extends SamplingMessage); it is normalized to a list here and
    /// always serialized as an array. Only valid sampling content blocks are permitted.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonConverter(typeof(SamplingContentConverter))]
    public required IReadOnlyList<Content> Content { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("stopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }

    /// <summary>Reserved protocol metadata (CreateMessageResult extends Result).</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

#endregion

#region Elicitation (#14)

/// <summary>
/// Handles user elicitation requests from MCP servers.
/// </summary>
public interface IElicitationHandler
{
    Task<ElicitResult> HandleAsync(ElicitRequest request, CancellationToken cancellationToken);
}

public sealed record ElicitRequest
{
    /// <summary>
    /// Elicitation mode: "form" (default when omitted) collects input against
    /// <see cref="RequestedSchema"/>; "url" (MCP 2025-11-25) directs the user to
    /// <see cref="Url"/> and correlates the interaction via <see cref="ElicitationId"/>.
    /// </summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [SinceRevision("2025-11-25")]
    public string? Mode { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>The requested schema (form mode only). Absent for URL-mode elicitation.</summary>
    [JsonPropertyName("requestedSchema")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RequestedSchema { get; init; }

    /// <summary>Opaque identifier correlating a URL-mode elicitation (URL mode only).</summary>
    [JsonPropertyName("elicitationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [SinceRevision("2025-11-25")]
    public string? ElicitationId { get; init; }

    /// <summary>The URL the user should visit to provide input (URL mode only).</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [SinceRevision("2025-11-25")]
    public string? Url { get; init; }

    /// <summary>True when this is a URL-mode elicitation request.</summary>
    [JsonIgnore]
    public bool IsUrlMode => string.Equals(Mode, "url", StringComparison.Ordinal);

    /// <summary>Build a form-mode elicitation request from a typed schema.</summary>
    public static ElicitRequest Form(string message, ElicitationSchema schema) =>
        new()
        {
            Mode = "form",
            Message = message,
            RequestedSchema = McpJsonDefaults.ToElement(schema)
        };

    /// <summary>Build a URL-mode elicitation request (MCP 2025-11-25).</summary>
    public static ElicitRequest ForUrl(string message, string elicitationId, string url) =>
        new()
        {
            Mode = "url",
            Message = message,
            ElicitationId = elicitationId,
            Url = url
        };
}

public sealed record ElicitResult
{
    [JsonPropertyName("action")]
    public required string Action { get; init; } // "accept", "decline", "cancel"

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }

    public static ElicitResult Accept(JsonElement content) =>
        new() { Action = "accept", Content = content };

    public static ElicitResult Decline() =>
        new() { Action = "decline" };

    public static ElicitResult Cancel() =>
        new() { Action = "cancel" };
}

#endregion
