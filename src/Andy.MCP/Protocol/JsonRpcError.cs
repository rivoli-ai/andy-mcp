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

    /// <summary>Create the 2025-11-25 error requesting URL-based user interaction.</summary>
    public static JsonRpcError UrlElicitationRequired(UrlElicitationRequiredData data, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Elicitations.Any(e => e.Mode != "url" || e.RequestedSchema is not null ||
            string.IsNullOrEmpty(e.ElicitationId) || !Uri.TryCreate(e.Url, UriKind.Absolute, out _)))
            throw new ArgumentException("All required elicitations must be complete URL-mode requests.", nameof(data));
        return new JsonRpcError
        {
            Code = McpErrorCodes.UrlElicitationRequired,
            Message = message ?? "URL elicitation required",
            Data = McpJsonDefaults.ToElement(data)
        };
    }

    /// <summary>Unknown wire fields retained for protocol extensions.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
