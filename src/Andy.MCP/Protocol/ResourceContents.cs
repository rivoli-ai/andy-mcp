using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Base type for resource contents returned by resources/read and used in EmbeddedResource.
/// Discriminated by the presence of 'text' vs 'blob' field.
/// </summary>
[JsonConverter(typeof(ResourceContentsConverter))]
public abstract record ResourceContents
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Text-based resource contents.
/// </summary>
public sealed record TextResourceContents : ResourceContents
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Binary resource contents, base64-encoded.
/// </summary>
public sealed record BlobResourceContents : ResourceContents
{
    [JsonPropertyName("blob")]
    public required string Blob { get; init; }

    public static BlobResourceContents FromBytes(string uri, byte[] bytes, string? mimeType = null) =>
        new() { Uri = uri, Blob = Convert.ToBase64String(bytes), MimeType = mimeType };

    public byte[] ToBytes() => Convert.FromBase64String(Blob);
}

/// <summary>
/// Custom converter for ResourceContents that discriminates by 'text' vs 'blob' field presence.
/// </summary>
public sealed class ResourceContentsConverter : JsonConverter<ResourceContents>
{
    public override ResourceContents Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected JSON object for ResourceContents.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var hasText = root.TryGetProperty("text", out _);
        var hasBlob = root.TryGetProperty("blob", out _);

        if (hasText && hasBlob)
            throw new JsonException("ResourceContents cannot have both 'text' and 'blob' fields.");

        var rawJson = root.GetRawText();

        if (hasText)
            return JsonSerializer.Deserialize<TextResourceContents>(rawJson, ConverterlessOptions(options))!;

        if (hasBlob)
            return JsonSerializer.Deserialize<BlobResourceContents>(rawJson, ConverterlessOptions(options))!;

        throw new JsonException("ResourceContents must have either 'text' or 'blob' field.");
    }

    public override void Write(Utf8JsonWriter writer, ResourceContents value, JsonSerializerOptions options)
    {
        var converterless = ConverterlessOptions(options);
        switch (value)
        {
            case TextResourceContents text:
                JsonSerializer.Serialize(writer, text, converterless);
                break;
            case BlobResourceContents blob:
                JsonSerializer.Serialize(writer, blob, converterless);
                break;
            default:
                throw new JsonException($"Unknown ResourceContents type: {value.GetType().Name}");
        }
    }

    private static JsonSerializerOptions ConverterlessOptions(JsonSerializerOptions options)
    {
        var newOptions = new JsonSerializerOptions(options);
        for (int i = newOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (newOptions.Converters[i] is ResourceContentsConverter)
                newOptions.Converters.RemoveAt(i);
        }
        return newOptions;
    }
}
