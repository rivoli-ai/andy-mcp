using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Base type for all MCP content blocks. Discriminated by the 'type' field.
/// Used in tool results, prompt messages, sampling, and embedded resources.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonDerivedType(typeof(AudioContent), "audio")]
[JsonDerivedType(typeof(ResourceLink), "resource_link")]
[JsonDerivedType(typeof(EmbeddedResource), "resource")]
[JsonDerivedType(typeof(ToolUseContent), "tool_use")]
[JsonDerivedType(typeof(ToolResultContent), "tool_result")]
public abstract record Content
{
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Annotations? Annotations { get; init; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Text content block.
/// </summary>
public sealed record TextContent : Content
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }

    public TextContent() { }
    public TextContent(string text) => Text = text;
}

/// <summary>
/// Image content block with base64-encoded data.
/// </summary>
public sealed record ImageContent : Content
{
    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; init; }

    public static ImageContent FromBytes(byte[] bytes, string mimeType) =>
        new() { Data = Convert.ToBase64String(bytes), MimeType = mimeType };

    public byte[] ToBytes() => Convert.FromBase64String(Data);
}

/// <summary>
/// Audio content block with base64-encoded data.
/// </summary>
public sealed record AudioContent : Content
{
    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("mimeType")]
    public required string MimeType { get; init; }

    public static AudioContent FromBytes(byte[] bytes, string mimeType) =>
        new() { Data = Convert.ToBase64String(bytes), MimeType = mimeType };

    public byte[] ToBytes() => Convert.FromBase64String(Data);
}

/// <summary>
/// A link to a resource by URI. The client can fetch the resource separately.
/// </summary>
public sealed record ResourceLink : Content
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; init; }
}

/// <summary>
/// An embedded resource with inline content (text or blob).
/// </summary>
public sealed record EmbeddedResource : Content
{
    [JsonPropertyName("resource")]
    public required ResourceContents Resource { get; init; }
}

/// <summary>
/// Tool use content block, used in sampling conversations to represent tool calls.
/// </summary>
public sealed record ToolUseContent : Content
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("input")]
    public required JsonElement Input { get; init; }
}

/// <summary>
/// Tool result content block, used in sampling conversations to represent tool call results.
/// </summary>
public sealed record ToolResultContent : Content
{
    [JsonPropertyName("toolUseId")]
    public required string ToolUseId { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Content>? Content { get; init; }

    [JsonPropertyName("structuredContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StructuredContent { get; init; }

    [JsonPropertyName("isError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }
}
