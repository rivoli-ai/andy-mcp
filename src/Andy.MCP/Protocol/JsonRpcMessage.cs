using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Base type for all JSON-RPC 2.0 messages.
/// Deserialization is handled by <see cref="JsonRpcMessageConverter"/> which discriminates
/// based on the presence of 'id', 'method', 'result', and 'error' fields.
/// </summary>
[JsonConverter(typeof(JsonRpcMessageConverter))]
public abstract record JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
}

/// <summary>
/// A JSON-RPC 2.0 request message. Has both 'id' and 'method'.
/// Can be sent in either direction (client→server or server→client).
/// </summary>
public sealed record JsonRpcRequest : JsonRpcMessage
{
    [JsonPropertyName("id")]
    public required RequestId Id { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }

    /// <summary>
    /// Deserialize Params into a typed object.
    /// </summary>
    public T? GetParams<T>(JsonSerializerOptions? options = null) where T : class
    {
        if (Params is null) return null;
        return JsonSerializer.Deserialize<T>(Params.Value, options ?? McpJsonDefaults.Options);
    }
}

/// <summary>
/// A JSON-RPC 2.0 response message. Has 'id' and either 'result' or 'error' (never both).
/// </summary>
public sealed record JsonRpcResponse : JsonRpcMessage
{
    [JsonPropertyName("id")]
    public required RequestId Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; init; }

    [JsonIgnore]
    public bool IsSuccess => Error is null;

    [JsonIgnore]
    public bool IsError => Error is not null;

    /// <summary>
    /// Deserialize Result into a typed object.
    /// </summary>
    public T? GetResult<T>(JsonSerializerOptions? options = null)
    {
        if (Result is null) return default;
        return JsonSerializer.Deserialize<T>(Result.Value, options ?? McpJsonDefaults.Options);
    }

    public static JsonRpcResponse Success(RequestId id, JsonElement? result = null) =>
        new() { Id = id, Result = result ?? JsonSerializer.SerializeToElement(new { }) };

    public static JsonRpcResponse Failure(RequestId id, JsonRpcError error) =>
        new() { Id = id, Error = error };
}

/// <summary>
/// A JSON-RPC 2.0 notification message. Has 'method' but no 'id'.
/// Fire-and-forget: receiver MUST NOT respond.
/// </summary>
public sealed record JsonRpcNotification : JsonRpcMessage
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Params { get; init; }

    /// <summary>
    /// Deserialize Params into a typed object.
    /// </summary>
    public T? GetParams<T>(JsonSerializerOptions? options = null) where T : class
    {
        if (Params is null) return null;
        return JsonSerializer.Deserialize<T>(Params.Value, options ?? McpJsonDefaults.Options);
    }
}
