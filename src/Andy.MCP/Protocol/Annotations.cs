using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Annotations that can be attached to content blocks and resources.
/// Provides hints about audience, priority, and freshness.
/// </summary>
public sealed record Annotations
{
    /// <summary>
    /// Who the content is intended for: "user", "assistant", or both.
    /// </summary>
    [JsonPropertyName("audience")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Role>? Audience { get; init; }

    /// <summary>
    /// Priority from 0.0 (least important) to 1.0 (most important).
    /// </summary>
    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Priority { get; init; }

    /// <summary>
    /// ISO 8601 timestamp of when the content was last modified.
    /// </summary>
    [JsonPropertyName("lastModified")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastModified { get; init; }
}

/// <summary>
/// Target audience for content.
/// </summary>
[JsonConverter(typeof(RoleJsonConverter))]
public enum Role
{
    User,
    Assistant
}

public sealed class RoleJsonConverter : JsonConverter<Role>
{
    public override Role Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "user" => Role.User,
            "assistant" => Role.Assistant,
            _ => throw new JsonException($"Unknown role: '{value}'. Expected 'user' or 'assistant'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Role value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            Role.User => "user",
            Role.Assistant => "assistant",
            _ => throw new JsonException($"Unknown role value: {value}")
        });
    }
}
