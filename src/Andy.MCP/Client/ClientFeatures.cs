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
}

public sealed record ListRootsResult
{
    [JsonPropertyName("roots")]
    public required IReadOnlyList<Root> Roots { get; init; }
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
}

public sealed record SamplingMessage
{
    [JsonPropertyName("role")]
    public required Role Role { get; init; }

    [JsonPropertyName("content")]
    public required Content Content { get; init; }
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

    [JsonPropertyName("content")]
    public required Content Content { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("stopReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StopReason { get; init; }
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
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("requestedSchema")]
    public required JsonElement RequestedSchema { get; init; }
}

public sealed record ElicitResult
{
    [JsonPropertyName("action")]
    public required string Action { get; init; } // "accept", "decline", "cancel"

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    public static ElicitResult Accept(JsonElement content) =>
        new() { Action = "accept", Content = content };

    public static ElicitResult Decline() =>
        new() { Action = "decline" };

    public static ElicitResult Cancel() =>
        new() { Action = "cancel" };
}

#endregion
