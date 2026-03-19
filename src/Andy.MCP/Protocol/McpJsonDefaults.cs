using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Default JSON serialization options for MCP protocol messages.
/// Uses camelCase naming, ignores null values, and includes the custom converters.
/// </summary>
public static class McpJsonDefaults
{
    private static readonly Lazy<JsonSerializerOptions> _options = new(() =>
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonRpcMessageConverter());
        options.Converters.Add(new RequestIdJsonConverter());
        options.Converters.Add(new NullableRequestIdJsonConverter());
        return options;
    });

    /// <summary>
    /// Shared, pre-configured serializer options for MCP messages.
    /// Thread-safe after first access.
    /// </summary>
    public static JsonSerializerOptions Options => _options.Value;

    /// <summary>
    /// Serialize a JSON-RPC message to a UTF-8 string.
    /// </summary>
    public static string Serialize(JsonRpcMessage message) =>
        JsonSerializer.Serialize(message, Options);

    /// <summary>
    /// Deserialize a UTF-8 JSON string to a JSON-RPC message.
    /// </summary>
    public static JsonRpcMessage Deserialize(string json) =>
        JsonSerializer.Deserialize<JsonRpcMessage>(json, Options)
        ?? throw new JsonRpcParseException("Deserialization returned null.");

    /// <summary>
    /// Serialize a value to a JsonElement for embedding in params/result.
    /// </summary>
    public static JsonElement ToElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options);
}

/// <summary>
/// Converter for Nullable&lt;RequestId&gt; used in Meta.ProgressToken.
/// </summary>
internal sealed class NullableRequestIdJsonConverter : JsonConverter<RequestId?>
{
    public override RequestId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        return reader.TokenType switch
        {
            JsonTokenType.String => (RequestId)reader.GetString()!,
            JsonTokenType.Number => (RequestId)reader.GetInt64(),
            _ => throw new JsonException($"Unexpected token type {reader.TokenType} for RequestId.")
        };
    }

    public override void Write(Utf8JsonWriter writer, RequestId? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Value.IsString)
            writer.WriteStringValue(value.Value.AsString());
        else
            writer.WriteNumberValue(value.Value.AsNumber());
    }
}
