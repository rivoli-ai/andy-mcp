using Andy.MCP.Protocol;

namespace Andy.MCP.Configuration;

/// <summary>
/// Configuration options for the MCP client, bindable from appsettings.json.
/// </summary>
public sealed class McpClientOptions
{
    public const string SectionName = "McpClient";

    /// <summary>
    /// Client implementation info.
    /// </summary>
    public ImplementationConfig ClientInfo { get; set; } = new() { Name = "Andy.MCP", Version = "0.1.0" };

    /// <summary>
    /// Default request timeout.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to automatically reconnect after disconnection.
    /// </summary>
    public bool AutoReconnect { get; set; }

    /// <summary>
    /// Reconnection policy settings.
    /// </summary>
    public ReconnectPolicyConfig ReconnectPolicy { get; set; } = new();

    /// <summary>
    /// MCP server configurations.
    /// </summary>
    public List<McpServerConfig> Servers { get; set; } = [];

    public Implementation ToImplementation() => new(ClientInfo.Name, ClientInfo.Version);
}

/// <summary>
/// Serializable implementation info for config binding.
/// </summary>
public sealed class ImplementationConfig
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>
/// Configuration for a single MCP server connection.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>
    /// Unique name for this server connection.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Transport type: "stdio", "http", or "gateway".
    /// </summary>
    public string Transport { get; set; } = "stdio";

    // stdio options
    public string? Command { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    public Dictionary<string, string>? Environment { get; set; }

    // HTTP options
    public string? Url { get; set; }

    // Gateway options
    public string? GatewayUrl { get; set; }
    public string? AdapterName { get; set; }
}

/// <summary>
/// Reconnection policy configuration.
/// </summary>
public sealed class ReconnectPolicyConfig
{
    public int MaxRetries { get; set; } = 5;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public string Strategy { get; set; } = "ExponentialWithJitter";
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);
}
