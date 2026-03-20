using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// An icon for display purposes, used on tools, resources, prompts, and templates.
/// </summary>
public sealed record Icon
{
    /// <summary>
    /// Icon source URL.
    /// </summary>
    [JsonPropertyName("src")]
    public required string Source { get; init; }

    /// <summary>
    /// MIME type of the icon (e.g., "image/svg+xml", "image/png").
    /// </summary>
    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    /// <summary>
    /// Icon sizes (e.g., "48x48", "any").
    /// </summary>
    [JsonPropertyName("sizes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Sizes { get; init; }

    /// <summary>
    /// Icon theme (e.g., "light", "dark").
    /// </summary>
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; init; }
}
