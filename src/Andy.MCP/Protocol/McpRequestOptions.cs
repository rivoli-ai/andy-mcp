using System.Text.Json;
using System.Text.Json.Nodes;
using Andy.MCP.Client;

namespace Andy.MCP.Protocol;

/// <summary>Per-call idle timeout, absolute duration and progress observer.</summary>
public sealed record McpRequestOptions
{
    public TimeSpan? Timeout { get; init; }
    public TimeSpan? MaximumDuration { get; init; }
    public IProgress<McpProgress>? Progress { get; init; }
}

internal static class ExtensionMethods
{
    public static void RequireCustom(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var prefix = method.Split('/')[0];
        if (method.StartsWith("rpc.", StringComparison.Ordinal) || prefix is "initialize" or "ping" or "notifications" or
            "tools" or "resources" or "prompts" or "completion" or "logging" or "roots" or "sampling" or "elicitation" or "tasks")
            throw new ArgumentException("Use a typed API for standard MCP methods; reserved namespaces cannot be overridden.", nameof(method));
    }

    public static JsonElement? ObjectParameters(JsonElement? parameters)
    {
        if (parameters is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("MCP parameters must be an object.");
        return parameters;
    }
    public static JsonElement ObjectResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("MCP results must be an object.");
        return result;
    }

    public static JsonElement? WithProgress(JsonElement? parameters, RequestId token)
    {
        var node = parameters is { } p ? JsonNode.Parse(p.GetRawText()) as JsonObject : new JsonObject();
        if (node is null) throw new ArgumentException("Request parameters must be an object.");
        var meta = node["_meta"] as JsonObject;
        if (meta is null)
        {
            if (node["_meta"] is not null) throw new ArgumentException("Request _meta must be an object.");
            node["_meta"] = meta = new JsonObject();
        }
        meta["progressToken"] = JsonNode.Parse(JsonSerializer.Serialize(token, McpJsonDefaults.Options));
        return JsonSerializer.SerializeToElement(node);
    }
}
