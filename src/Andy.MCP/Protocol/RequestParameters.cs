using System.Text.Json;
using System.Text.Json.Serialization;
namespace Andy.MCP.Protocol;

/// <summary>Common request parameters, including metadata supported since the first MCP revision.</summary>
public record RequestParams
{
    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public record ResourceRequestParams : RequestParams
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}
public sealed record ReadResourceRequestParams : ResourceRequestParams;
public sealed record SubscribeRequestParams : ResourceRequestParams;
public sealed record UnsubscribeRequestParams : ResourceRequestParams;

public sealed record GetPromptRequestParams : RequestParams
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("arguments")]
    public IDictionary<string, string>? Arguments { get; init; }
}

public record NotificationParams
{
    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
public sealed record ResourceUpdatedParams : NotificationParams
{
    [JsonPropertyName("uri")]
    public required string Uri { get; init; }
}

/// <summary>Structured data for the URL-elicitation-required protocol error (-32042).</summary>
[SinceRevision("2025-11-25")]
public sealed record UrlElicitationRequiredData
{
    [JsonPropertyName("elicitations")]
    public required IReadOnlyList<Andy.MCP.Client.ElicitRequest> Elicitations { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
