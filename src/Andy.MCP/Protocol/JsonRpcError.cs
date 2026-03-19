using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// A JSON-RPC 2.0 error object returned in error responses.
/// </summary>
public sealed record JsonRpcError
{
    [JsonPropertyName("code")]
    public required int Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; init; }

    public static JsonRpcError ParseError(string? message = null) =>
        new() { Code = McpErrorCodes.ParseError, Message = message ?? "Parse error" };

    public static JsonRpcError InvalidRequest(string? message = null) =>
        new() { Code = McpErrorCodes.InvalidRequest, Message = message ?? "Invalid request" };

    public static JsonRpcError MethodNotFound(string? message = null) =>
        new() { Code = McpErrorCodes.MethodNotFound, Message = message ?? "Method not found" };

    public static JsonRpcError InvalidParams(string? message = null) =>
        new() { Code = McpErrorCodes.InvalidParams, Message = message ?? "Invalid params" };

    public static JsonRpcError InternalError(string? message = null) =>
        new() { Code = McpErrorCodes.InternalError, Message = message ?? "Internal error" };

    public static JsonRpcError ResourceNotFound(string? message = null) =>
        new() { Code = McpErrorCodes.ResourceNotFound, Message = message ?? "Resource not found" };
}
