using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Custom JSON converter that discriminates JSON-RPC 2.0 messages based on field presence:
/// - Has 'id' + 'method' → Request
/// - Has 'id' + ('result' or 'error') → Response
/// - Has 'method' without 'id' → Notification
///
/// MCP does NOT support JSON-RPC batch requests (arrays).
/// </summary>
public sealed class JsonRpcMessageConverter : JsonConverter<JsonRpcMessage>
{
    public override JsonRpcMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            throw new JsonRpcParseException("JSON-RPC batch requests (arrays) are not supported by MCP.");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonRpcParseException($"Expected JSON object, got {reader.TokenType}.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Validate jsonrpc field
        if (root.TryGetProperty("jsonrpc", out var jsonrpcProp))
        {
            var version = jsonrpcProp.GetString();
            if (version != "2.0")
                throw new JsonRpcParseException($"Unsupported JSON-RPC version: '{version}'. Expected '2.0'.");
        }
        else
        {
            throw new JsonRpcParseException("Missing required 'jsonrpc' field.");
        }

        var hasId = root.TryGetProperty("id", out var idProp);
        var hasMethod = root.TryGetProperty("method", out _);
        var hasResult = root.TryGetProperty("result", out _);
        var hasError = root.TryGetProperty("error", out _);

        // Reject null id
        if (hasId && idProp.ValueKind == JsonValueKind.Null)
            throw new JsonRpcParseException("Request 'id' must not be null.");

        // Reject response with both result and error
        if (hasResult && hasError)
            throw new JsonRpcParseException("Response must not contain both 'result' and 'error'.");

        var rawJson = root.GetRawText();

        if (hasId && hasMethod)
        {
            // Request: has both id and method
            return JsonSerializer.Deserialize<JsonRpcRequest>(rawJson, ConverterlessOptions(options))!;
        }

        if (hasId && (hasResult || hasError))
        {
            // Response: has id and either result or error
            return JsonSerializer.Deserialize<JsonRpcResponse>(rawJson, ConverterlessOptions(options))!;
        }

        if (hasMethod && !hasId)
        {
            // Notification: has method but no id
            return JsonSerializer.Deserialize<JsonRpcNotification>(rawJson, ConverterlessOptions(options))!;
        }

        throw new JsonRpcParseException("Unable to determine JSON-RPC message type. Expected request (id+method), response (id+result/error), or notification (method only).");
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcMessage value, JsonSerializerOptions options)
    {
        var converterless = ConverterlessOptions(options);

        switch (value)
        {
            case JsonRpcRequest request:
                JsonSerializer.Serialize(writer, request, converterless);
                break;
            case JsonRpcResponse response:
                JsonSerializer.Serialize(writer, response, converterless);
                break;
            case JsonRpcNotification notification:
                JsonSerializer.Serialize(writer, notification, converterless);
                break;
            default:
                throw new JsonException($"Unknown JsonRpcMessage type: {value.GetType().Name}");
        }
    }

    /// <summary>
    /// Creates options without this converter to prevent infinite recursion.
    /// </summary>
    private static JsonSerializerOptions ConverterlessOptions(JsonSerializerOptions options)
    {
        var newOptions = new JsonSerializerOptions(options);
        // Remove all JsonRpcMessageConverter instances to prevent recursion
        for (int i = newOptions.Converters.Count - 1; i >= 0; i--)
        {
            if (newOptions.Converters[i] is JsonRpcMessageConverter)
                newOptions.Converters.RemoveAt(i);
        }
        return newOptions;
    }
}
