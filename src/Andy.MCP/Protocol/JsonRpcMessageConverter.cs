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

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name))
                throw new JsonRpcParseException("Duplicate JSON-RPC field: " + property.Name);

        if (!root.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            throw new JsonRpcParseException("Expected JSON-RPC version 2.0.");

        var hasId = root.TryGetProperty("id", out var idProp);
        var hasMethod = root.TryGetProperty("method", out var method);
        var hasResult = root.TryGetProperty("result", out var result);
        var hasError = root.TryGetProperty("error", out var error);
        var hasParams = root.TryGetProperty("params", out var parameters);

        if (hasMethod)
        {
            if (method.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(method.GetString()))
                throw new JsonRpcParseException("Method must be a nonempty string.");
            if (hasResult || hasError)
                throw new JsonRpcParseException("A request or notification cannot contain a response.");
            if (hasParams && parameters.ValueKind != JsonValueKind.Object)
                throw new JsonRpcParseException("MCP params must be an object.");
        }
        else if (hasParams)
            throw new JsonRpcParseException("A response cannot contain params.");

        if (hasResult && (hasError || result.ValueKind != JsonValueKind.Object))
            throw new JsonRpcParseException("A result response must contain an object and no error.");
        if (hasError && (error.ValueKind != JsonValueKind.Object ||
            !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _) ||
            !error.TryGetProperty("message", out var errorMessage) || errorMessage.ValueKind != JsonValueKind.String))
            throw new JsonRpcParseException("An error must contain an integer code and string message.");

        // Errors without a readable ID must not be correlated to request zero.
        if (!hasMethod && hasError && (!hasId || idProp.ValueKind == JsonValueKind.Null))
            return new JsonRpcUncorrelatedError
            {
                Error = JsonSerializer.Deserialize<JsonRpcError>(error.GetRawText(), ConverterlessOptions(options))!
            };

        if (hasId && idProp.ValueKind != JsonValueKind.String &&
            !(idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out _)))
            throw new JsonRpcParseException("Request ID must be a string or integer.");

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
            case JsonRpcUncorrelatedError uncorrelated:
                JsonSerializer.Serialize(writer, uncorrelated, converterless);
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
