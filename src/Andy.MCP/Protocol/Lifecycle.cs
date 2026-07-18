using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// Parameters for the 'initialize' request sent from client to server.
/// </summary>
public sealed record InitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required ClientCapabilities Capabilities { get; init; }

    [JsonPropertyName("clientInfo")]
    public required Implementation ClientInfo { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Result of the 'initialize' request, returned by the server.
/// </summary>
public sealed record InitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required ServerCapabilities Capabilities { get; init; }

    [JsonPropertyName("serverInfo")]
    public required Implementation ServerInfo { get; init; }

    /// <summary>
    /// Optional instructions for the LLM on how to use this server.
    /// Injected into the LLM context, NOT shown to the user.
    /// </summary>
    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; init; }

    /// <summary>Reserved protocol metadata (_meta), preserved round-trip.</summary>
    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Meta { get; init; }
}

/// <summary>
/// Identifies an MCP client or server implementation.
/// </summary>
public sealed record Implementation
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>
    /// Optional human-readable description of what this implementation does (MCP 2025-11-25).
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Optional URL of the website for this implementation (MCP 2025-11-25).
    /// </summary>
    [JsonPropertyName("websiteUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebsiteUrl { get; init; }

    /// <summary>
    /// Optional icons for this implementation (MCP 2025-11-25).
    /// </summary>
    [JsonPropertyName("icons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Icon>? Icons { get; init; }

    public Implementation() { }

    [SetsRequiredMembers]
    public Implementation(string name, string version) { Name = name; Version = version; }
}

/// <summary>
/// Capabilities declared by the client during initialization.
/// </summary>
public sealed record ClientCapabilities
{
    [JsonPropertyName("roots")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RootsCapability? Roots { get; init; }

    [JsonPropertyName("sampling")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Sampling { get; init; }

    [JsonPropertyName("elicitation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Elicitation { get; init; }

    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Experimental { get; init; }

    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>
/// Capabilities declared by the server during initialization.
/// </summary>
public sealed record ServerCapabilities
{
    [JsonPropertyName("prompts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ListChangedCapability? Prompts { get; init; }

    [JsonPropertyName("resources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourcesCapability? Resources { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ListChangedCapability? Tools { get; init; }

    [JsonPropertyName("logging")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Logging { get; init; }

    [JsonPropertyName("completions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyCapability? Completions { get; init; }

    [JsonPropertyName("experimental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Experimental { get; init; }

    [JsonPropertyName("extensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}

/// <summary>
/// An empty capability object, indicating support without sub-options.
/// </summary>
public sealed record EmptyCapability;

/// <summary>
/// Capability with optional listChanged flag.
/// </summary>
public sealed record ListChangedCapability
{
    [JsonPropertyName("listChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; init; }
}

/// <summary>
/// Roots capability with optional listChanged flag.
/// </summary>
public sealed record RootsCapability
{
    [JsonPropertyName("listChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; init; }
}

/// <summary>
/// Resources capability with optional subscribe and listChanged flags.
/// </summary>
public sealed record ResourcesCapability
{
    [JsonPropertyName("subscribe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Subscribe { get; init; }

    [JsonPropertyName("listChanged")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ListChanged { get; init; }
}
