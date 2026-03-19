using System.Text.Json;
using System.Text.Json.Serialization;

namespace Andy.MCP.Protocol;

/// <summary>
/// The reserved _meta field for protocol-level metadata on request params and results.
/// Contains optional progressToken and extensible additional properties.
/// </summary>
public sealed record Meta
{
    /// <summary>
    /// Progress token for tracking long-running operations.
    /// The receiver may send progress notifications referencing this token.
    /// Can be a string or number, represented as a RequestId.
    /// </summary>
    [JsonPropertyName("progressToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RequestId? ProgressToken { get; init; }

    /// <summary>
    /// Additional metadata properties. Keys prefixed with 'modelcontextprotocol' or 'mcp'
    /// are reserved for the protocol.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extensions { get; init; }
}
