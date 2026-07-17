using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Serializes the sampling message/result <c>content</c> field, which under MCP 2025-11-25 is a
/// union of a single content block or an array of content blocks
/// (<c>SamplingMessageContentBlock | SamplingMessageContentBlock[]</c>).
///
/// Reading accepts both the scalar and array forms. Writing always emits the array form, which is
/// valid wherever multi-block sampling content is (2025-03-26 through 2025-11-25); the in-memory
/// model is normalized to a list either way.
///
/// Only the content blocks valid for sampling are permitted: text, image, audio, tool_use, and
/// tool_result. Resource links and embedded resources are rejected on both read and write.
/// </summary>
public sealed class SamplingContentConverter : JsonConverter<IReadOnlyList<Content>>
{
    public override IReadOnlyList<Content> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            throw new JsonException("Sampling content must not be null.");

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = JsonSerializer.Deserialize<List<Content>>(ref reader, options)
                ?? throw new JsonException("Sampling content array could not be read.");
            foreach (var block in list)
                EnsureValidSamplingBlock(block);
            return list;
        }

        // Scalar form: a single content block.
        var single = JsonSerializer.Deserialize<Content>(ref reader, options)
            ?? throw new JsonException("Sampling content must not be null.");
        EnsureValidSamplingBlock(single);
        return new[] { single };
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<Content> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var block in value)
        {
            EnsureValidSamplingBlock(block);
            JsonSerializer.Serialize(writer, block, options);
        }
        writer.WriteEndArray();
    }

    /// <summary>
    /// Validates that a content block is one of the types permitted in sampling messages and
    /// results per the 2025-11-25 schema.
    /// </summary>
    public static void EnsureValidSamplingBlock(Content block)
    {
        if (block is not (TextContent or ImageContent or AudioContent or ToolUseContent or ToolResultContent))
            throw new JsonException(
                $"'{block.GetType().Name}' is not a valid sampling content block. Sampling permits only " +
                "text, image, audio, tool_use, and tool_result content.");
    }
}
