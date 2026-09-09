using System.Collections.Concurrent;
using System.Threading.Channels;
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
    private readonly SemaphoreSlim _lifecycle = new(1);
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly Func<McpServerConfig, IClientTransport>? _transportFactory;
    private sealed class Connection(McpServerConfig config)
    {
        public McpServerConfig Config { get; } = config;
        public CancellationTokenSource Stop { get; } = new();
        public Channel<bool> Changes { get; } = Channel.CreateBounded<bool>(1);
        public EventHandler<TransportDisconnectedEventArgs>? Handler;
        public Task Worker { get; set; } = Task.CompletedTask;
    }

    internal McpConnectionManager(McpClientOptions options, ILogger<McpConnectionManager>? logger,
        Func<McpServerConfig, IClientTransport> transportFactory) : this(options, logger)
    {
        _transportFactory = transportFactory;
    }

    public McpConnectionManager(McpClientOptions options, ILogger<McpConnectionManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReconnectPolicy.MaxRetries < 0 || options.ReconnectPolicy.BaseDelay < TimeSpan.Zero || options.ReconnectPolicy.MaxDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to MCP server '{Name}'", config.Name);
            }
        }
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var name in _connections.Keys.ToArray())
        {
            try { await RemoveServerAsync(name).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disconnecting from '{Name}'", name); }
        }
    }

    public async Task AddServerAsync(McpServerConfig config, CancellationToken cancellationToken = default)
    {
        await ConnectServerAsync(config, cancellationToken);
        _logger.LogInformation("Added and connected to MCP server '{Name}'", config.Name);
    }

    public async Task RemoveServerAsync(string name)
    {
        Connection? connection;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_connections.TryRemove(name, out connection)) return;
            connection.Stop.Cancel();
            await RemoveClientAsync(name, connection).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
        await connection.Worker.ConfigureAwait(false);
        connection.Stop.Dispose();
    }

    private async Task RemoveClientAsync(string name, Connection connection)
    {
        if (_clients.TryRemove(name, out var client))
        {
            client.Disconnected -= connection.Handler;
            await client.DisposeAsync().ConfigureAwait(false);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list tools from '{Name}'", name);
            }
        }

        return allTools;
    }

    private async Task ConnectServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connections.ContainsKey(config.Name)) throw new InvalidOperationException($"Server '{config.Name}' is already configured.");
            var connection = new Connection(config);
            try
            {
                await ConnectClientAsync(connection, cancellationToken).ConfigureAwait(false);
                _connections[config.Name] = connection;
                connection.Worker = RecoverAsync(connection);
            }
            catch { connection.Stop.Dispose(); throw; }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ConnectClientAsync(Connection connection, CancellationToken ct)
    {
        var config = connection.Config;
        var transport = _transportFactory is not null ? _transportFactory(config)
            : config.Transport.Equals("gateway", StringComparison.OrdinalIgnoreCase)
                ? await CreateGatewayTransportAsync(config, ct).ConfigureAwait(false) : CreateTransport(config);
        var clientOptions = new Client.McpClientOptions { ClientInfo = _options.ToImplementation(), RequestTimeout = _options.RequestTimeout };
        var client = await McpClient.ConnectAsync(transport, clientOptions, cancellationToken: ct).ConfigureAwait(false);
        _clients[config.Name] = client;
        connection.Handler = (sender, _) =>
        {
            if (ReferenceEquals(GetClient(config.Name), sender)) connection.Changes.Writer.TryWrite(true);
        };
        client.Disconnected += connection.Handler;
        if (client.Session.State == McpSessionState.Closed) connection.Changes.Writer.TryWrite(true);
    }

    private async Task RecoverAsync(Connection connection)
    {
        var ct = connection.Stop.Token;
        var name = connection.Config.Name;
        try
        {
            while (await connection.Changes.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (connection.Changes.Reader.TryRead(out _)) { }
                await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
                try { await RemoveClientAsync(name, connection).ConfigureAwait(false); }
                finally { _lifecycle.Release(); }
                if (!_options.AutoReconnect) continue;
                for (var retry = 0; retry < _options.ReconnectPolicy.MaxRetries; retry++)
                {
                    var policy = _options.ReconnectPolicy;
                    var factor = policy.Strategy.Equals("Linear", StringComparison.OrdinalIgnoreCase) ? retry + 1
                        : policy.Strategy.Equals("Fixed", StringComparison.OrdinalIgnoreCase) ? 1 : Math.Pow(2, Math.Min(retry, 30));
                    var delay = Math.Min(policy.MaxDelay.TotalMilliseconds, policy.BaseDelay.TotalMilliseconds * factor);
                    if (policy.Strategy.Contains("Jitter", StringComparison.OrdinalIgnoreCase)) delay *= 0.5 + Random.Shared.NextDouble() * 0.5;
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, delay)), ct).ConfigureAwait(false);
                    await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!_connections.TryGetValue(name, out var current) || !ReferenceEquals(current, connection)) return;
                        await ConnectClientAsync(connection, ct).ConfigureAwait(false);
                        _logger.LogInformation("Reconnected MCP server '{Name}'", name);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _logger.LogWarning(ex, "MCP reconnect attempt {Attempt} failed for '{Name}'", retry + 1, name); }
                    finally { _lifecycle.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static async Task<IClientTransport> CreateGatewayTransportAsync(McpServerConfig config, CancellationToken ct)
    {
        using var http = new HttpClient();
        var registry = new Gateway.McpGatewayClient(http, new Gateway.McpGatewayOptions
        {
            RegistryUri = new Uri(config.GatewayUrl ?? throw new InvalidOperationException("GatewayUrl is required."))
        });
        var entries = await registry.ListAsync(ct).ConfigureAwait(false);
        var matches = entries.Where(entry => entry.Status == Gateway.GatewayStatus.Active
            && string.Equals(entry.Name, config.AdapterName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException($"Expected one active gateway registration named '{config.AdapterName}', found {matches.Length}.");
        var endpoint = new Uri(matches[0].Endpoint);
        Gateway.McpGatewayClient.ValidateEndpoint(endpoint);
        return new StreamableHttpClientTransport(new StreamableHttpClientTransportOptions { Endpoint = endpoint });
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
            _ => throw new InvalidOperationException($"Unknown transport type '{config.Transport}' for server '{config.Name}'.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
        }
        finally { _lifecycle.Release(); }
        await DisconnectAllAsync().ConfigureAwait(false);
    }
}
