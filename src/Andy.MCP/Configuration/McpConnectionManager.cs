using System.Collections.Concurrent;
using Andy.MCP.Client;
using Andy.MCP.Protocol;
using Andy.MCP.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Andy.MCP.Configuration;

/// <summary>
/// Manages connections to multiple MCP servers.
/// </summary>
public interface IMcpConnectionManager : IAsyncDisposable
{
    /// <summary>
    /// Get a connected client by server name.
    /// </summary>
    McpClient? GetClient(string name);

    /// <summary>
    /// Get all connected client names.
    /// </summary>
    IReadOnlyList<string> ConnectedServers { get; }

    /// <summary>
    /// Connect to all configured servers.
    /// </summary>
    Task ConnectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from all servers.
    /// </summary>
    Task DisconnectAllAsync();

    /// <summary>
    /// Add and connect to a server at runtime.
    /// </summary>
    Task AddServerAsync(McpServerConfig config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove and disconnect from a server at runtime.
    /// </summary>
    Task RemoveServerAsync(string name);

    /// <summary>
    /// Aggregate all tools from all connected servers.
    /// </summary>
    Task<IReadOnlyList<(string serverName, Tool tool)>> ListAllToolsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IMcpConnectionManager"/>.
/// </summary>
public sealed class McpConnectionManager : IMcpConnectionManager
{
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly McpClientOptions _options;
    private readonly ILogger<McpConnectionManager> _logger;
    private bool _disposed;

    public McpConnectionManager(McpClientOptions options, ILogger<McpConnectionManager>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<McpConnectionManager>.Instance;
    }

    public McpClient? GetClient(string name) =>
        _clients.TryGetValue(name, out var client) ? client : null;

    public IReadOnlyList<string> ConnectedServers =>
        _clients.Keys.ToList();

    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var config in _options.Servers)
        {
            try
            {
                await ConnectServerAsync(config, cancellationToken);
                _logger.LogInformation("Connected to MCP server '{Name}'", config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to MCP server '{Name}'", config.Name);
            }
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var kvp in _clients)
        {
            try
            {
                if (_clients.TryRemove(kvp.Key, out var client))
                    await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disconnecting from '{Name}'", kvp.Key);
            }
        }
    }

    public async Task AddServerAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        await ConnectServerAsync(config, cancellationToken);
        _logger.LogInformation("Added and connected to MCP server '{Name}'", config.Name);
    }

    public async Task RemoveServerAsync(string name)
    {
        if (_clients.TryRemove(name, out var client))
        {
            await client.DisposeAsync();
            _logger.LogInformation("Removed MCP server '{Name}'", name);
        }
    }

    public async Task<IReadOnlyList<(string serverName, Tool tool)>> ListAllToolsAsync(
        CancellationToken cancellationToken = default)
    {
        var allTools = new List<(string, Tool)>();

        foreach (var (name, client) in _clients)
        {
            try
            {
                if (!client.Session.HasServerCapability("tools")) continue;

                var tools = await client.ListToolsAsync(cancellationToken);
                foreach (var tool in tools)
                {
                    allTools.Add((name, tool));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list tools from '{Name}'", name);
            }
        }

        return allTools;
    }

    private async Task ConnectServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        var transport = CreateTransport(config);
        var clientOptions = new Client.McpClientOptions
        {
            ClientInfo = _options.ToImplementation(),
            RequestTimeout = _options.RequestTimeout
        };

        var client = await McpClient.ConnectAsync(transport, clientOptions, cancellationToken: cancellationToken);

        if (!_clients.TryAdd(config.Name, client))
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"Server '{config.Name}' is already connected.");
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig config)
    {
        return config.Transport.ToLowerInvariant() switch
        {
            "stdio" => new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = config.Command ?? throw new InvalidOperationException($"Server '{config.Name}': 'Command' is required for stdio transport."),
                Arguments = config.Arguments,
                WorkingDirectory = config.WorkingDirectory,
                EnvironmentVariables = config.Environment
            }),
            "http" => new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions
            {
                Endpoint = new Uri(config.Url ?? throw new InvalidOperationException($"Server '{config.Name}': 'Url' is required for http transport.")),
            }),
            "gateway" => throw new NotSupportedException($"Gateway transport not yet implemented. Server: '{config.Name}'"),
            _ => throw new InvalidOperationException($"Unknown transport type '{config.Transport}' for server '{config.Name}'.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAllAsync();
    }
}
